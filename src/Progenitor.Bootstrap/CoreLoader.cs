using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using BepInEx;
using Progenitor.Ui;

namespace Progenitor.Bootstrap;

/// <summary>
/// Loads <c>BepInEx/progenitor-core/Progenitor.dll</c> with <see cref="AssemblyLoadContext.LoadFromStream"/>
/// into a collectible ALC (one generation at a time) and invokes <c>Progenitor.Runtime.Install(host)</c>.
/// <see cref="RequestReload"/> queues an in-process Uninstall → ALC unload → Load → Install
/// and returns immediately; <see cref="TickReload"/> runs it on FrameTicker ticks.
/// Step E: <see cref="CoreWatch"/> requests the same cascade with reason <c>watch</c>
/// when <c>Progenitor.dll.next</c> has settled. Bootstrap must not compile-reference Core.
/// </summary>
public static class CoreLoader
{
    private static readonly object ReloadGate = new();
    private static readonly CoreReloadFrameDriver ReloadDriver = new();
    private static LiveOps? _activeOps;
    private static AssemblyLoadContext? _alc;
    private static WeakReference? _previousAlc;
    private static bool _alcUnloadRequested;
    private static ICoreHost? _host;
    private static int _generation;
    /// <summary>
    /// Bytes of the last generation that <see cref="LoadAndInstallBytes"/> succeeded
    /// for. Restore after a broken N+1 on disk reloads these, not the new file.
    /// Survives <c>DropPublishedCore</c> (it is not an ALC pin).
    /// </summary>
    private static byte[]? _lastGoodBytes;
    /// <summary>
    /// Protocol of <see cref="_lastGoodBytes"/> at the Install that remembered
    /// them. Restore after a Bootstrap protocol bump must not write an older
    /// protocol over a newer on-disk file.
    /// </summary>
    private static int _lastGoodProtocol;
    /// <summary>
    /// Exact image last passed to <c>LoadFromStream</c>, including a failed
    /// Install. Quarantine compares this to the on-disk file so a later promote
    /// is not renamed away as "the failed Core".
    /// </summary>
    private static byte[]? _lastLoadedBytes;

    /// <summary>AUTOUPDATE5: the image last passed to LoadFromStream (the start-loop guard's clean-exit check).</summary>
    internal static byte[]? LoadedImage => _lastLoadedBytes;

    public static Assembly? Assembly { get; private set; }

    /// <summary>1-based generation of the currently loaded Core, or 0 if none.</summary>
    public static int Generation => _generation;

    /// <summary>Name of the current collectible context, or null when Core is not loaded.</summary>
    public static string? AlcName => _alc?.Name;

    /// <summary>
    /// ALC name native pins should tag at Keep time. During <c>Install</c> the new context
    /// is not published yet (<c>_alc</c> still holds generation N-1 or is null); Bind still
    /// needs the generation the dispatcher belongs to, which is <c>_pendingAlcName</c>.
    /// Never returns the previous generation's name while a new Install is in flight.
    /// </summary>
    internal static string? PinGeneration => CurrentPinGeneration;

    /// <summary>
    /// Keep-time generation id. Prefers the in-flight Install name, then the published
    /// ALC, then the 1-based generation number. Empty only when Core has never loaded.
    /// </summary>
    internal static string CurrentPinGeneration
    {
        get
        {
            if (!string.IsNullOrEmpty(_pendingAlcName)) return _pendingAlcName;
            if (!string.IsNullOrEmpty(_alc?.Name)) return _alc.Name;
            if (_generation >= CoreReloadPolicy.FirstGeneration)
                return CoreReloadPolicy.AlcName(_generation);
            return "";
        }
    }

    private static string? _pendingAlcName;

    /// <summary>True when a previous generation's ALC has not been collected yet.</summary>
    public static bool PreviousGenerationAlive => _previousAlc is { IsAlive: true };

    /// <summary>True while a swap is scheduled or mid-collect. Watch and a second <c>core_reload</c> refuse.</summary>
    public static bool IsReloading => ReloadDriver.IsBusy;

    /// <summary>
    /// First load from disk into a collectible ALC. Called from Bootstrap <c>Load</c>.
    /// A missing file logs <see cref="CoreLayout.MissingLog"/> and does not scan plugins.
    /// Promotes <c>Progenitor.dll.next</c> before the first read, even when the
    /// Step E watcher is off — a protocol bump restarts both processes with Core
    /// staged as <c>.next</c> and must take effect this launch.
    /// </summary>
    public static bool TryLoadAndInstall(ICoreHost host)
    {
        if (host == null) throw new ArgumentNullException(nameof(host));
        _host = host;

        _ = CoreReloadPolicy.ShouldPromoteOnFirstLoad(ProcessHost.WatchCore);
        TryPromoteStagedCore();

        if (!TryReadBytes(out var bytes, out var error)
            || !CoreImagePolicy.HasCompleteCliMetadata(bytes))
        {
            if (TryRestorePrev())
            {
                if (!TryReadBytes(out bytes, out error) || !CoreImagePolicy.HasCompleteCliMetadata(bytes))
                {
                    ProcessHost.Log?.LogError(
                        string.IsNullOrEmpty(error) ? "Core file is not a valid PE image." : error);
                    CoreWatch.NoteLiveImageTried();
                    return false;
                }
            }
            else
            {
                ProcessHost.Log?.LogError(
                    string.IsNullOrEmpty(error) ? "Core file is not a valid PE image." : error);
                CoreWatch.NoteLiveImageTried();
                return false;
            }
        }

        int generation = CoreReloadPolicy.NextGeneration(_generation);
        if (LoadAndInstallBytes(bytes, generation, host, out var loadError))
            return true;

        if (CoreReloadPolicy.IsProtocolMismatchMessage(loadError))
            return TryLoadMatchingCoreAfterProtocolMismatch(host, bytes);

        CoreWatch.NoteLiveImageTried();
        return false;
    }

    /// <summary>
    /// First-load protocol refuse is not "restore last-good" when last-good is
    /// the same old protocol. Prefer a still-staged <c>.next</c> or a live file
    /// that is no longer the failed image; otherwise stay Core-less and keep
    /// the watch armed.
    /// </summary>
    private static bool TryLoadMatchingCoreAfterProtocolMismatch(ICoreHost host, byte[] failedBytes)
    {
        try
        {
            TryPromoteStagedCore();
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("Core promote after protocol mismatch: " + e.Message);
        }

        if (TryReadStagedNextBytes(out var nextBytes)
            && CoreImagePolicy.HasCompleteCliMetadata(nextBytes)
            && !CoreReloadPolicy.ImagesMatch(nextBytes, failedBytes))
        {
            int generation = CoreReloadPolicy.NextGeneration(_generation);
            if (LoadAndInstallBytes(nextBytes, generation, host, out var nextError)
                && !CoreReloadPolicy.IsProtocolMismatchMessage(nextError))
                return true;
        }

        if (TryReadBytes(out var liveBytes, out _)
            && CoreImagePolicy.HasCompleteCliMetadata(liveBytes)
            && !CoreReloadPolicy.ImagesMatch(liveBytes, failedBytes))
        {
            int generation = CoreReloadPolicy.NextGeneration(_generation);
            if (LoadAndInstallBytes(liveBytes, generation, host, out var liveError)
                && !CoreReloadPolicy.IsProtocolMismatchMessage(liveError))
                return true;
        }

        string stay = CoreReloadPolicy.FormatProtocolMismatchStayCoreLess(CoreLayout.ProtocolVersion);
        ProcessHost.Log?.LogError(stay);
        CoreReloadStatus.Record(
            0,
            CoreReloadStatus.OutcomeFailed,
            stay,
            alcName: null,
            inFlight: false);
        CoreWatch.NoteLiveImageTried();
        return false;
    }

    private static bool TryReadStagedNextBytes(out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            string next = CoreWatchPolicy.NextPath(CoreLayout.DllPath(Paths.BepInExRootPath));
            if (!File.Exists(next)) return false;
            bytes = File.ReadAllBytes(next);
            return bytes.Length > 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Queue an in-process Core swap and return immediately with <c>scheduled gen=N</c>.
    /// The cascade runs on Bootstrap's own FrameTicker tick (<see cref="TickReload"/>)
    /// after Drain, so no plugin or Core frame is on the stack at collect time. Never
    /// blocks the caller on <c>WorkQueue.Run</c>. A previous generation that did not
    /// collect is restored by loading gen N's bytes into a fresh ALC (same as a
    /// collecting dry-run), never by keeping the old runtime object alive. If that
    /// fresh load fails too, the process is left Core-less (logged <c>CORE-LESS</c>).
    /// <paramref name="reason"/> may contain the token <c>dryRun</c> or JSON
    /// <c>{"dryRun":true}</c> to sweep, diagnose, and collect-test without stacking N+1.
    /// </summary>
    public static string RequestReload(string reason) =>
        RequestReload(reason, CoreReloadPolicy.ParseDryRun(reason));

    /// <summary>Same as <see cref="RequestReload(string)"/> with an explicit dry-run flag.</summary>
    public static string RequestReload(string reason, bool dryRun)
    {
        reason ??= "";
        if (dryRun && !CoreReloadPolicy.ParseDryRun(reason))
            reason = reason.Length == 0 ? "dryRun" : reason + " dryRun";
        try
        {
            string scheduled;
            bool scheduledOk;
            lock (ReloadGate)
            {
                try { ReloaderHost.PollPluginLatch(); }
                catch (Exception e)
                {
                    ProcessHost.Log?.LogWarning("plugin-alc latch poll: " + e.Message);
                }
                if (ReloaderHost.LatchedPluginAlcsAlive())
                {
                    scheduled = CoreReloadPolicy.RefusePluginAlcAlive(ReloaderHost.LatchedPluginAlcNamesLive());
                    CoreReloadStatus.Record(
                        _generation,
                        CoreReloadStatus.OutcomeRefused,
                        reason,
                        _alc?.Name,
                        inFlight: false);
                    ProcessHost.Log?.LogInfo(scheduled);
                    return scheduled;
                }
                // Job FINJ1: a plugin that registered its own IL2CPP types and references Core keeps
                // this generation alive for the life of the process. Refused here, before anything
                // is scheduled; the text carries the restart marker, so the watcher parks on it.
                var lifetimeBound = ReloaderHost.CoreBoundProcessLifetimeFiles();
                if (lifetimeBound.Count > 0)
                {
                    scheduled = Il2CppInjectionPolicy.RefuseCoreSwap(lifetimeBound);
                    CoreReloadStatus.Record(
                        _generation,
                        CoreReloadStatus.OutcomeRefused,
                        reason,
                        _alc?.Name,
                        inFlight: false);
                    ProcessHost.Log?.LogWarning(scheduled);
                    return scheduled;
                }
                scheduled = ReloadDriver.Request(
                    _generation,
                    _alc?.Name,
                    PreviousGenerationAlive,
                    reason);
                scheduledOk = scheduled.StartsWith("scheduled", StringComparison.OrdinalIgnoreCase);
                CoreReloadStatus.Record(
                    _generation,
                    scheduledOk ? CoreReloadStatus.OutcomeScheduled : CoreReloadStatus.OutcomeRefused,
                    reason,
                    _alc?.Name,
                    inFlight: scheduledOk);
            }
            ProcessHost.Log?.LogInfo(scheduled);
            if (ProcessHost.CoreTickInProgress && scheduledOk)
            {
                ProcessHost.Log?.LogInfo(CoreReloadPolicy.ScheduledInsideTick);
            }
            return scheduled;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogError($"Core RequestReload threw: {e}");
            string failed = CoreReloadPolicy.FormatFailed(_generation, "request", e.GetType().Name + ": " + e.Message, oldRunning: Assembly != null);
            CoreReloadStatus.Record(_generation, CoreReloadStatus.OutcomeFailed, reason, _alc?.Name, inFlight: false);
            return failed;
        }
    }

    /// <summary>
    /// Drive a scheduled swap from Bootstrap <c>ProcessHost.Tick</c> after Drain. First
    /// tick runs Freeze through <c>ALC.Unload</c>; later ticks pulse GC and then restore
    /// or load. Must not be called from inside <c>ICoreRuntime.Tick</c> or a plugin
    /// <c>MainThread</c> lambda — that is the stack pin this method exists to avoid.
    /// </summary>
    internal static void TickReload()
    {
        if (!ReloadDriver.IsBusy) return;
        lock (ReloadGate)
        {
            try
            {
                if (ReloadDriver.IsScheduled && _activeOps == null)
                {
                    string reason = ReloadDriver.ScheduledReason ?? "";
                    bool dryRun = CoreReloadPolicy.ParseDryRun(reason);
                    if (!dryRun)
                    {
                        bool hadNext = CoreWatch.HasPendingNext();
                        bool promoted = TryPromoteStagedCore();
                        bool coreLoaded = _generation >= CoreReloadPolicy.FirstGeneration && Assembly != null;
                        if (CoreWatchPolicy.CancelScheduledReload(hadNext, promoted, coreLoaded))
                        {
                            ReloadDriver.Reset();
                            CoreReloadStatus.Record(
                                _generation,
                                CoreReloadStatus.OutcomeRefused,
                                reason,
                                _alc?.Name,
                                inFlight: false);
                            return;
                        }
                    }
                    _activeOps = new LiveOps
                    {
                        DryRun = dryRun,
                        Reason = reason,
                        HolderScanWide = ProcessHost.HolderScanWide
                            || CoreReloadPolicy.ParseDryRunWide(reason),
                    };
                }

                var ops = _activeOps;
                if (ops == null) return;
                var result = ReloadDriver.Tick(ops, alcAlive: () => PreviousGenerationAlive);
                if (result == null) return;
                ProcessHost.Log?.LogInfo(result.Value.Summary);
                CoreReloadStatus.Record(result.Value, ops.Reason);
                // Do not keep the result, the LiveOps host, or any ICoreRuntime /
                // assembly / ALC across the next swap. Generation 2 was installed
                // through this path; a leftover ops/result pin is why reload 2
                // never collected (evidence 61).
                _activeOps = null;
            }
            catch (Exception e)
            {
                ProcessHost.Log?.LogError($"Core reload tick threw: {e}");
                try { _activeOps?.Unfreeze(); }
                catch (Exception u) { ProcessHost.Log?.LogWarning($"Core reload unfreeze after throw: {u.Message}"); }
                _activeOps = null;
                ReloadDriver.Reset();
            }
        }
    }

    public static void Uninstall()
    {
        SnapshotContent(out _);
        InvokeRuntime(CoreLayout.UninstallMethod, args: null, out _);
        Assembly = null;
        _generation = 0;
    }

    internal static bool SnapshotContent(out string error)
    {
        error = "";
        try
        {
            if (!InvokeRuntime(CoreLayout.SnapshotMethod, args: new object?[] { _host }, out error))
                return false;
            return true;
        }
        catch (Exception e)
        {
            error = e.Message;
            ProcessHost.Log?.LogWarning($"Core snapshot failed: {e.Message}");
            return false;
        }
    }

    internal static void NotifyLiveWorldAfterCoreSwap()
    {
        try
        {
            InvokeRuntime(CoreLayout.NotifyLiveWorldMethod, args: null, out _);
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning($"Core NotifyLiveWorldAfterCoreSwap failed: {e.Message}");
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool LoadAndInstallBytes(byte[] bytes, int generation, ICoreHost host, out string error)
    {
        error = "";
        string alcName = CoreReloadPolicy.AlcName(generation);

        // Job FRLD: a Core generation is the other automatic code-loading path, so it gets
        // the same identity line and the same optional allowlist gate as the reload folder.
        // Checked before the ALC is created so a refused image never reaches LoadFromStream.
        string coreHash = ReloadAllowPolicy.Sha256Hex(bytes);
        long coreSize = bytes.LongLength;
        var coreAllow = ReloadAllowStore.Decide(CoreLayout.FileName, coreHash);
        if (!ReloadAllowPolicy.MayLoad(coreAllow))
        {
            error = ReloadAllowPolicy.FormatRefusedCore(CoreLayout.FileName, coreHash, coreSize, coreAllow);
            ProcessHost.Log?.LogError(error);
            return false;
        }

        CoreLoadContext? alc = null;
        int previousGen = _generation;
        var previousAsm = Assembly;
        var previousAlc = _alc;
        // PinGeneration must be the NEW id before Install runs. KeepDelegate on
        // Harmony / receive / lifetime slots otherwise tags gen N+1 pins with gen N's
        // name (evidence 61: gen 2 release finds nothing). Do not publish Assembly/_alc
        // until Install succeeds.
        _pendingAlcName = alcName;
        _generation = generation;
        bool installInvoked = false;
        string failError = "";
        RememberLoadedBytes(bytes);
        try
        {
            alc = new CoreLoadContext(alcName);
            alc.AttachResolving(OnCoreResolving);

            Assembly asm;
            string? pdbPath = null;
            try
            {
                pdbPath = Path.ChangeExtension(CoreLayout.DllPath(Paths.BepInExRootPath), ".pdb");
            }
            catch
            {
                pdbPath = null;
            }

            using (var stream = new MemoryStream(bytes, writable: false))
            {
                if (!string.IsNullOrEmpty(pdbPath) && File.Exists(pdbPath))
                {
                    using var symbols = new MemoryStream(File.ReadAllBytes(pdbPath), writable: false);
                    asm = alc.LoadFromStream(stream, symbols);
                }
                else
                    asm = alc.LoadFromStream(stream);
            }

            var type = asm.GetType(CoreLayout.RuntimeTypeName);
            if (type == null)
            {
                failError = "Core loaded but type " + CoreLayout.RuntimeTypeName + " was not found.";
                error = failError;
                ProcessHost.Log?.LogError(failError);
                return false;
            }

            int coreProtocol = CoreReloadPolicy.ReadProtocolVersion(asm);
            if (!CoreReloadPolicy.AcceptProtocol(CoreLayout.ProtocolVersion, coreProtocol, out var protoError))
            {
                failError = protoError;
                error = protoError;
                ProcessHost.Log?.LogError(protoError);
                return false;
            }

            var install = type.GetMethod(CoreLayout.InstallMethod, BindingFlags.Public | BindingFlags.Static);
            if (install == null)
            {
                failError = "Core loaded but " + CoreLayout.RuntimeTypeName + "." + CoreLayout.InstallMethod + " was not found.";
                error = failError;
                ProcessHost.Log?.LogError(failError);
                return false;
            }

            installInvoked = true;
            install.Invoke(null, new object?[] { host });
            Assembly = asm;
            _alc = alc;
            _generation = generation;
            _alcUnloadRequested = false;
            RememberLastGoodBytes(bytes);
            ForgetPrevFile();
            // A collect-fail restore loads N into a new ALC while the previous
            // context may still be alive. Keep that weak so MayStart refuses a
            // stacked swap until restart. A dead weak (successful collect) is cleared.
            _previousAlc = _previousAlc is { IsAlive: true } ? _previousAlc : null;
            _pendingAlcName = null;
            try { WorkQueue.UnretireNamed(alcName); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Core LoadAndInstall Unretire: {e.Message}"); }
            ProcessHost.Log?.LogInfo(
                ReloadAllowPolicy.FormatCoreLoad(CoreLayout.FileName, alcName, coreHash, coreSize, coreAllow));
            string coreAsmName;
            try { coreAsmName = asm.GetName().Name ?? ""; }
            catch { coreAsmName = ""; }
            ProcessHost.Log?.LogInfo(
                ReloadAllowPolicy.FormatCoreLoadVerbose(CoreLayout.FileName, coreHash, coreSize, coreAsmName));
            return true;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            failError = e.InnerException.GetType().Name + ": " + e.InnerException.Message;
            error = failError;
            ProcessHost.Log?.LogError($"Core Install threw: {e.InnerException}");
            Assembly = previousAsm;
            _alc = previousAlc;
            _generation = previousGen;
            _pendingAlcName = previousAlc?.Name;
            return false;
        }
        catch (Exception e)
        {
            failError = e.GetType().Name + ": " + e.Message;
            error = failError;
            ProcessHost.Log?.LogError($"Core LoadFromStream/Install failed: {e}");
            Assembly = previousAsm;
            _alc = previousAlc;
            _generation = previousGen;
            _pendingAlcName = previousAlc?.Name;
            return false;
        }
        finally
        {
            if (alc != null && !ReferenceEquals(_alc, alc))
            {
                // PinGeneration was advanced before Install. Roll it back so a failed
                // GetType / Install does not leave CurrentPinGeneration pointing at a
                // generation that was never published.
                Assembly = previousAsm;
                _alc = previousAlc;
                _generation = previousGen;
                _pendingAlcName = previousAlc?.Name;
                // R14 finding 2: Install may have filled Harmony / receive / lifetime /
                // CoreTick slots and KeepDelegate'd pins under alcName. Unload without
                // clearing those is a jump into a dying collectible ALC on the next
                // SystemBase.Update. Slots first, then pins, then Unload.
                try
                {
                    ProcessHost.Log?.LogInfo(
                        CoreReloadPolicy.FormatAbandonFailedInstall(generation, alcName));
                    ClearFailedInstallSlots();
                    ReleaseFailedInstallGeneration(alcName);
                }
                catch (Exception e)
                {
                    ProcessHost.Log?.LogWarning($"abandon failed install {alcName}: {e.Message}");
                }
                try
                {
                    // Job FLATCH4 (R47 finding 3): the clear is the unload primitive's argument,
                    // not this method's discipline. It ran a few lines up and runs again here,
                    // because ClearFailedInstallSlots is idempotent and because this is the one
                    // ALC unload that does not go through the cascade's NullRuntime — the path
                    // whose omission of the mask slot nothing could fail on.
                    CoreReloadCollect.PrepareAndUnload(alc, ClearFailedInstallSlots);
                    LogClearError(alcName);
                }
                catch { /* best-effort of a failed generation */ }
                // MayStart must refuse another swap until this context dies (or restart).
                // Do not overwrite a still-alive previous weak (collect-fail restore).
                // A failed GetType / missing Install never KeepDelegate'd anything;
                // latching that empty context wedges RequestReload (R16 finding 7).
                if (CoreReloadPolicy.ShouldLatchFailedInstallContext(installInvoked, failError)
                    && _previousAlc is not { IsAlive: true })
                    _previousAlc = CoreReloadPolicy.CreateGenerationWeak(alc);
                // Quarantine is the orchestrator's job (AbandonFailedInstall) so a
                // first-boot miss does not rename the only Core file, and a cascade
                // miss does not quarantine last-good twice.
                if (string.IsNullOrEmpty(error))
                    error = failError;
                ProcessHost.Log?.LogInfo(
                    CoreReloadPolicy.StepLog(generation, "failed generation " + alcName + " unload requested; slots empty"));
            }
        }
    }

    /// <summary>
    /// Null every Bootstrap slot that may hold a delegate from a collectible Core
    /// generation. Harmony, receive, lifetime, LateFrame, Ui dispatch/keys/capture, the
    /// input mask apply slot, and the MainThread/tick adapter. Names match
    /// <see cref="CoreReloadPolicy.FailedInstallSlots"/> (R24 finding 6). Idempotent.
    ///
    /// <para>The name says failed install and the job is wider: this is the pre-unload
    /// clear for <b>both</b> paths that unload a Core ALC — the failed-install
    /// <c>finally</c> in <c>LoadAndInstallBytes</c> and <c>LiveOps.UnloadAlc</c> — and
    /// since job FLATCH4 it is also what <c>CoreReloadCollect.PrepareAndUnload</c> is
    /// handed, so no unload runs without it. It is not renamed because
    /// <c>FailedInstallSlots</c>, the log lines and three reviews all use the term; what
    /// changed is that the coverage is enforced rather than stated.</para>
    ///
    /// <para>R47 finding 3: <c>UiMaskDetour.ClearMask</c> was missing here. FLATCH3 added
    /// the mask slot with clears in <c>NullRuntime</c> and <c>MarkUninstalled</c> and not
    /// in this one, which is the only unload that skips <c>NullRuntime</c>. The review
    /// could not find a way to reach it with the slot filled and neither could this job
    /// (<c>EnsureMask</c> is reached only from <c>GameInputBlocker.Register</c>, which
    /// <c>Core.Install</c> never calls), so the defect fixed here is the false guarantee,
    /// not an observed pin.</para>
    /// </summary>
    internal static void ClearFailedInstallSlots()
    {
        try { HarmonySlots.ReleaseHeld(); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"HarmonySlots.ReleaseHeld after failed install: {e.Message}"); }
        try { ReceiveDetours.TrySetClient(null); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"receive.client null after failed install: {e.Message}"); }
        try { ReceiveDetours.TrySetServer(null); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"receive.server null after failed install: {e.Message}"); }
        try { ProcessHost.SetLifetimeQuitting(null); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"lifetime.quitting null after failed install: {e.Message}"); }
        try { ProcessHost.SetLifetimeFrame(null); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"lifetime.frame null after failed install: {e.Message}"); }
        try { ProcessHost.SetLateFrame(null); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"lifetime.lateframe null after failed install: {e.Message}"); }
        try
        {
            ProcessHost.CoreTick = null;
            ProcessHost.SweepPlugin = null;
            ProcessHost.CoreAttached = false;
            ProcessHost.SetCoreLog(null);
        }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"MainThread adapter null after failed install: {e.Message}"); }
        try { UiSlots.ClearAll(); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"UiSlots.ClearAll after failed install: {e.Message}"); }
        try { UiSlots.ClearDispatch(); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"UiSlots.ClearDispatch after failed install: {e.Message}"); }
        try { UiActionSlot.ClearAll(); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"UiActionSlot.ClearAll after failed install: {e.Message}"); }
        try { UiInputContext.ClearKeyDispatch(); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"UiInputContext.ClearKeyDispatch after failed install: {e.Message}"); }
        try { UiInputContext.ClearCapture(); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"UiInputContext.ClearCapture after failed install: {e.Message}"); }
        try { UiMaskDetour.ClearMask(); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"UiMaskDetour.ClearMask after failed install: {e.Message}"); }
    }

    /// <summary>
    /// Report a pre-unload clear that threw. <see cref="CoreReloadCollect"/> keeps the
    /// exception instead of rethrowing, because a clear that failed must not be able to
    /// skip the <c>Unload</c> — but it must not disappear either: a generation that later
    /// refuses to collect is read against this line first.
    /// </summary>
    private static void LogClearError(string? alcName)
    {
        var e = CoreReloadCollect.LastClearError;
        if (e == null) return;
        ProcessHost.Log?.LogWarning(
            "pre-unload slot clear for " + (alcName ?? "?") + " threw: " + e.Message
            + "; unloading anyway (slots may still reference this generation).");
    }

    /// <summary>
    /// Drop MainThread work owned by the failed ALC name and free its
    /// <c>NativePinRegistry</c> entries. Call after <see cref="ClearFailedInstallSlots"/>
    /// so a trampoline cannot jump into a pin that was just freed. Never
    /// <c>ReleaseAll</c>.
    /// </summary>
    internal static int ReleaseFailedInstallGeneration(string? alcName)
    {
        try { WorkQueue.DropOwnedBy(plugin: null, assembly: null, alcName); }
        catch (Exception e) { ProcessHost.Log?.LogWarning($"DropOwnedBy after failed install: {e.Message}"); }
        try
        {
            int before = NativePinRegistry.CountFor(alcName);
            int dropped = NativePinRegistry.ReleaseGeneration(alcName);
            int after = NativePinRegistry.CountFor(alcName);
            ProcessHost.Log?.LogInfo(CoreReloadPolicy.FormatPinRelease(alcName, before, after, dropped));
            return dropped;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning($"ReleaseGeneration after failed install: {e.Message}");
            return 0;
        }
    }

    private static void RememberLastGoodBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
            return;
        var copy = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        _lastGoodBytes = copy;
        try { _lastGoodProtocol = CoreReloadPolicy.ReadProtocolVersion(Assembly); }
        catch { _lastGoodProtocol = 0; }
        ProcessHost.Log?.LogInfo(CoreReloadPolicy.FormatLastGoodBytes(copy.Length));
    }

    private static void RememberLoadedBytes(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0)
        {
            _lastLoadedBytes = null;
            return;
        }
        var copy = new byte[bytes.Length];
        Buffer.BlockCopy(bytes, 0, copy, 0, bytes.Length);
        _lastLoadedBytes = copy;
    }

    private static Assembly? OnCoreResolving(AssemblyLoadContext context, AssemblyName name)
    {
        _ = context;
        return CoreAssemblyResolver.Resolve(name.Name, AssemblyLoadContext.Default.Assemblies);
    }

    private static bool TryReadBytes(out byte[] bytes, out string error)
    {
        bytes = Array.Empty<byte>();
        error = "";
        string path;
        try
        {
            path = CoreLayout.DllPath(Paths.BepInExRootPath);
        }
        catch (Exception e)
        {
            error = $"{CoreLayout.MissingLog} ({e.Message})";
            return false;
        }

        if (!File.Exists(path))
        {
            error = CoreLayout.MissingLog;
            return false;
        }

        try
        {
            bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0)
            {
                error = "Core file is empty: " + path;
                return false;
            }
            return true;
        }
        catch (Exception e)
        {
            error = "Core read failed: " + e.Message;
            return false;
        }
    }

    private static bool TryPromoteStagedCore()
    {
        bool requireProtocol = _generation >= CoreReloadPolicy.FirstGeneration && Assembly != null;
        try { return SelfUpdate.TryPromoteCoreNow(requireProtocol); }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning($"Core promote before reload: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Confirm <paramref name="bytes"/> is a complete CLI PE (section table +
    /// metadata header fit), then inspect it in a throwaway collectible ALC for
    /// <c>Progenitor.Runtime.Install</c> and an acceptable protocol. Does not
    /// invoke Install. Used so a truncated <c>.next</c> is never rotated over the
    /// live dll and never unloads the live generation (evidence 77).
    /// </summary>
    internal static bool TryValidateCoreImage(byte[] bytes, out string error) =>
        TryValidateCoreImage(bytes, requireMatchingProtocol: true, out error);

    /// <summary>
    /// Same as <see cref="TryValidateCoreImage(byte[], out string)"/> with an
    /// explicit protocol gate. First-load promote skips the handshake so a
    /// throwaway inspect that reads protocol 0 cannot quarantine a matching Core.
    /// </summary>
    internal static bool TryValidateCoreImage(byte[] bytes, bool requireMatchingProtocol, out string error)
    {
        error = "";
        if (!CoreImagePolicy.HasCompleteCliMetadata(bytes))
        {
            error = CoreImagePolicy.IsPeImage(bytes)
                ? "truncated PE (section table or CLI metadata extends past file length)"
                : "not a PE image or truncated";
            return false;
        }

        var alc = new CoreLoadContext("progenitor-core-inspect");
        try
        {
            alc.AttachResolving(OnCoreResolving);
            Assembly asm;
            using (var stream = new MemoryStream(bytes, writable: false))
                asm = alc.LoadFromStream(stream);
            var runtime = asm.GetType(CoreLayout.RuntimeTypeName);
            bool hasRuntime = runtime != null;
            bool hasInstall = runtime?.GetMethod(
                CoreLayout.InstallMethod,
                BindingFlags.Public | BindingFlags.Static) != null;
            int protocol = CoreReloadPolicy.ReadProtocolVersion(asm);
            return CoreImagePolicy.AcceptNextImage(
                bytes, hasRuntime, hasInstall, CoreLayout.ProtocolVersion, protocol, requireMatchingProtocol, out error);
        }
        catch (Exception e)
        {
            error = "Core image inspect failed: " + e.Message;
            return false;
        }
        finally
        {
            // Job FLATCH4: the named door for a context that never invoked Install, so
            // "this one needs no slot clear" is written down where the unload happens
            // rather than implied by the absence of one.
            try { CoreReloadCollect.UnloadNeverInstalled(alc); } catch { /* inspect */ }
        }
    }

    /// <summary>
    /// Rename an invalid <c>.next</c> to a failed name without touching the live dll.
    /// </summary>
    internal static string? QuarantineNextFile(string nextPath, string error)
    {
        if (string.IsNullOrEmpty(nextPath) || !File.Exists(nextPath))
            return null;
        DateTime utc = DateTime.UtcNow;
        string name = CoreWatchPolicy.FailedFileName(utc);
        string dest;
        try
        {
            string? dir = Path.GetDirectoryName(nextPath);
            dest = string.IsNullOrEmpty(dir)
                ? name
                : Path.Combine(dir, name);
            if (File.Exists(dest))
                File.Delete(dest);
            File.Move(nextPath, dest);
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("quarantine .next failed: " + e.Message);
            return null;
        }
        ProcessHost.Log?.LogWarning(CoreImagePolicy.FormatRejectedNext(error));
        ProcessHost.Log?.LogWarning(CoreImagePolicy.FormatQuarantinedNext(name));
        return name;
    }

    private static void ForgetPrevFile()
    {
        try
        {
            if (!CoreImagePolicy.MayDeletePrev(newImageProven: true)) return;
            string prev = CoreImagePolicy.PrevPath(CoreLayout.DllPath(Paths.BepInExRootPath));
            if (!File.Exists(prev)) return;
            File.Delete(prev);
            ProcessHost.Log?.LogInfo(CoreImagePolicy.FormatDeletedPrev());
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("Core: could not delete .prev: " + e.Message);
        }
    }

    /// <summary>
    /// First boot: if the live dll is missing or not a complete CLI PE and
    /// <c>.prev</c> is, put <c>.prev</c> back. One log line.
    /// </summary>
    private static bool TryRestorePrev()
    {
        try
        {
            string live = CoreLayout.DllPath(Paths.BepInExRootPath);
            string prev = CoreImagePolicy.PrevPath(live);
            if (!File.Exists(prev)) return false;

            byte[] prevBytes;
            try { prevBytes = File.ReadAllBytes(prev); }
            catch { return false; }
            if (!CoreImagePolicy.HasCompleteCliMetadata(prevBytes)) return false;

            bool liveInvalid = true;
            if (File.Exists(live))
            {
                try
                {
                    byte[] liveBytes = File.ReadAllBytes(live);
                    liveInvalid = !CoreImagePolicy.HasCompleteCliMetadata(liveBytes);
                }
                catch
                {
                    liveInvalid = true;
                }
            }

            if (!CoreImagePolicy.ShouldRestorePrev(liveInvalid, prevIsValidPe: true))
                return false;

            if (File.Exists(live))
            {
                string junk = live + ".invalid";
                try { if (File.Exists(junk)) File.Delete(junk); } catch { /* leftover */ }
                File.Replace(prev, live, junk);
                try { File.Delete(junk); } catch { /* junk copy of the truncated live */ }
            }
            else
                File.Move(prev, live);

            ProcessHost.Log?.LogWarning(CoreImagePolicy.FormatRestoredPrev());
            return true;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("Core: could not restore .prev: " + e.Message);
            return false;
        }
    }

    private static bool InvokeRuntime(string method, object?[]? args, out string error)
    {
        error = "";
        var asm = Assembly;
        if (asm == null)
        {
            error = "Core is not loaded.";
            return false;
        }
        try
        {
            var type = asm.GetType(CoreLayout.RuntimeTypeName);
            var mi = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (mi == null)
            {
                error = CoreLayout.RuntimeTypeName + "." + method + " was not found.";
                return false;
            }
            mi.Invoke(null, args);
            return true;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            error = e.InnerException.ToString();
            ProcessHost.Log?.LogWarning($"Core {method} threw: {e.InnerException.Message}");
            return false;
        }
        catch (Exception e)
        {
            error = e.ToString();
            ProcessHost.Log?.LogWarning($"Core {method} failed: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Bridge / watch dump: generation, lastReload, watch state, last pin-diag summary.
    /// </summary>
    public static object DescribeStatus()
    {
        lock (ReloadGate)
        {
            int gen = _generation;
            string? alc = _alc?.Name;
            bool previous = PreviousGenerationAlive;
            bool busy = ReloadDriver.IsBusy;
            int coreProtocol = 0;
            try { coreProtocol = CoreReloadPolicy.ReadProtocolVersion(Assembly); }
            catch { coreProtocol = 0; }
            CoreReloadStatus.SetWatch(
                ProcessHost.WatchCore,
                CoreWatch.HasPendingNext(),
                ProcessHost.IsHostedChild,
                ProcessHost.IsHostingPrivateGame,
                busy,
                previous);
            try { CoreReloadStatus.SetWaitingPlugins(ReloaderHost.WaitingRows()); }
            catch (Exception e) { ProcessHost.Log?.LogWarning("reload_status waiting plugins: " + e.Message); }
            try { CoreReloadStatus.SetProcessLifetimePlugins(ReloaderHost.ProcessLifetimeRows()); }
            catch (Exception e) { ProcessHost.Log?.LogWarning("reload_status process-lifetime plugins: " + e.Message); }
            return CoreReloadStatus.ToDump(gen, alc, CoreLayout.ProtocolVersion, coreProtocol);
        }
    }

    /// <summary>
    /// Rename the on-disk Core that just failed Install / protocol so the watcher
    /// cannot loop, then write last-good bytes back. Idempotent when the file is gone.
    /// Compares the live file to the image that was actually loaded: a later
    /// promote is left alone (BepInEx script-reloader plugins / MonoMod HookGen never
    /// quarantine a file they did not load).
    /// </summary>
    internal static string? QuarantineFailedCoreFile(int generation, string error)
    {
        string path;
        try
        {
            path = CoreLayout.DllPath(Paths.BepInExRootPath);
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("quarantine: " + e.Message);
            return null;
        }

        if (!File.Exists(path))
            return null;

        byte[]? onDisk = null;
        try { onDisk = File.ReadAllBytes(path); }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("quarantine read: " + e.Message);
            return null;
        }

        if (!CoreReloadPolicy.MayQuarantineOnDisk(_lastLoadedBytes, onDisk))
        {
            ProcessHost.Log?.LogWarning(CoreReloadPolicy.FormatQuarantineSkipped());
            return null;
        }

        DateTime utc = DateTime.UtcNow;
        string name = CoreWatchPolicy.FailedFileName(utc);
        string dest = CoreWatchPolicy.QuarantinePath(path, utc);
        bool restored = false;
        bool mayRestore = CoreReloadPolicy.MayRestoreLastGood(
            _lastGoodBytes,
            _lastGoodProtocol,
            CoreLayout.ProtocolVersion,
            _lastLoadedBytes,
            onDisk);

        if (!mayRestore || !CoreImagePolicy.MayMoveLiveAway(_lastGoodBytes is { Length: > 0 }))
        {
            ProcessHost.Log?.LogError(CoreReloadPolicy.FormatFailedInstall(generation, error, name, restoredLastGood: false));
            return name;
        }

        string tmp = CoreImagePolicy.LastGoodTempPath(path);
        try
        {
            if (File.Exists(tmp))
                File.Delete(tmp);
            File.WriteAllBytes(tmp, _lastGoodBytes!);
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning(CoreImagePolicy.FormatAtomicLastGoodFailed(e.Message));
            ProcessHost.Log?.LogError(CoreReloadPolicy.FormatFailedInstall(generation, error, name, restoredLastGood: false));
            return null;
        }

        try
        {
            if (File.Exists(dest))
                File.Delete(dest);
            File.Replace(tmp, path, dest);
            restored = true;
            ForgetPrevFile();
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning(CoreImagePolicy.FormatAtomicLastGoodFailed(e.Message));
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* leftover temp */ }
            ProcessHost.Log?.LogError(CoreReloadPolicy.FormatFailedInstall(generation, error, name, restoredLastGood: false));
            return null;
        }

        ProcessHost.Log?.LogError(CoreReloadPolicy.FormatFailedInstall(generation, error, name, restored));
        return name;
    }

    private sealed class LiveOps : ICoreReloadOps
    {
        public bool DryRun { get; set; }
        public string Reason { get; set; } = "";
        public bool HolderScanWide { get; set; }
        public bool PreviousGenerationAlive => CoreLoader.PreviousGenerationAlive;
        public bool LatchedPluginAlcsAlive => ReloaderHost.LatchedPluginAlcsAlive();
        public IReadOnlyList<string> LatchedPluginAlcNames() => ReloaderHost.LatchedPluginAlcNamesLive();
        public void LatchUncollectedPluginAlcs() => ReloaderHost.LatchAlivePluginAlcs();
        public bool AlreadyReloading => false; // CoreReloadFrameDriver.IsBusy is the in-flight guard
        public int CurrentGeneration => _generation;
        public string? CurrentAlcName => _alc?.Name;
        public bool HasPublishedAssembly => Assembly != null;
        public bool YieldPluginAlcWait => true;
        public bool YieldUnityFlush => CoreReloadPolicy.UnityFlushFrames > 0;
        public int CurrentFrame => ProcessHost.FrameCount;
        public bool PluginAlcsNeverCollectable => ReloaderHost.AnyDetourPinned();
        public IReadOnlyList<string> CoreBoundProcessLifetimePlugins() => ReloaderHost.CoreBoundProcessLifetimeFiles();
        public byte[]? LastGoodBytes => _lastGoodBytes;
        private List<string> _watchedPluginAlcs = new();
        public IReadOnlyList<string> WatchedPluginAlcNames() => _watchedPluginAlcs;
        public IReadOnlyList<string> AlivePluginAlcNames() => ReloaderHost.ListCollectiblePluginAlcNames();

        public void DropOldGenerationRefs()
        {
            // Reload-path slots (Harmony forwards, receive, lifetime, AfterCall
            // snapshots) must drop the same way the boot path does. Leaving them
            // is the gen-2 pin: generation 1 was installed by ProcessHost.Install
            // and Uninstall cleared those slots; generation 2 was installed by
            // LoadAndInstall and a missed clear keeps its delegates.
            try { ClearFailedInstallSlots(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"DropOldGenerationRefs slots: {e.Message}"); }
            try { HarmonyPatches.ClearAfterSnapshots(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"DropOldGenerationRefs AfterCall: {e.Message}"); }
            DropPublishedCore();
        }

        public bool TryReadCoreBytes(out byte[] bytes, out string error) => TryReadBytes(out bytes, out error);

        public void Freeze()
        {
            ProcessHost.CoreAttached = false;
            ReceiveDetours.Freeze();
            ReceiveDetours.WaitForIdle();
        }

        public void Unfreeze()
        {
            ReceiveDetours.Unfreeze();
            // Abort-before-Uninstall: keep the old generation answering Scan. After
            // NullRuntime, CoreTick is gone and CoreAttached stays false (empty-slot policy).
            if (Assembly != null && ProcessHost.CoreTick != null)
                ProcessHost.CoreAttached = true;
        }

        public int UnloadPlugins()
        {
            if (!ControlFormService.BeforeUnload())
                throw new InvalidOperationException("Core reload refused: temporary player control could not be restored.");
            int n = ReloaderHost.UnloadAllReverse();
            _watchedPluginAlcs = ReloaderHost.ListCollectiblePluginAlcNames();
            return n;
        }

        public void DiagnosePins()
        {
            CoreReloadStatus.ResetPinDiag();
            Action<string> log = m =>
            {
                ProcessHost.Log?.LogInfo(m);
                if (m != null && m.StartsWith("pin-diag", StringComparison.Ordinal))
                    CoreReloadStatus.AppendPin(m);
            };
            try { CorePinDiagnostics.LogAll(Assembly, _alc?.Name, log); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Core pin-diag: {e.Message}"); }
            try { InvokeRuntime(CoreLayout.DiagnosePinsMethod, args: null, out _); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Core DiagnosePins: {e.Message}"); }
        }

        public int PulseCollect()
        {
            var clock = Stopwatch.StartNew();
            CoreReloadPolicy.PulseCollectThenCheck(stillAlive: null);
            clock.Stop();
            int ms = (int)clock.ElapsedMilliseconds;
            if (ms >= 10)
                ProcessHost.Log?.LogWarning(CoreReloadPolicy.FormatCollectHitch(ms));
            return ms;
        }

        public void QuarantineFailedCore(int generation, string error) =>
            QuarantineFailedCoreFile(generation, error);

        public bool SnapshotContent(out string error) => CoreLoader.SnapshotContent(out error);

        public bool UninstallCore(out string error)
        {
            error = "";
            if (!ControlFormService.BeforeUnload())
            { error = "Temporary player control recovery is pending."; return false; }
            var asm = Assembly;
            if (asm == null)
            {
                error = "Core is not loaded.";
                return true;
            }
            try
            {
                CoreLoader.SnapshotContent(out _);
                // Keep ECS / chat / connect serving generation N until ALC.Unload.
                // Uninstall ClearAll's the slots; RestoreHeld puts the same Core
                // forwards back so the collectible window is not vanilla.
                try { HarmonySlots.HoldForUnload(); }
                catch (Exception e) { ProcessHost.Log?.LogWarning($"HarmonySlots.HoldForUnload: {e.Message}"); }
                if (!InvokeRuntime(CoreLayout.UninstallMethod, args: null, out error))
                {
                    try { HarmonySlots.RestoreHeld(); }
                    catch (Exception e) { ProcessHost.Log?.LogWarning($"HarmonySlots.RestoreHeld: {e.Message}"); }
                    return false;
                }
                try { HarmonySlots.RestoreHeld(); }
                catch (Exception e) { ProcessHost.Log?.LogWarning($"HarmonySlots.RestoreHeld: {e.Message}"); }
                try
                {
                    CorePinDiagnostics.UnpatchCoreHarmony(m => ProcessHost.Log?.LogInfo(m));
                    int leftover = CorePinDiagnostics.CountCoreHarmonyLeftovers(asm);
                    ProcessHost.Log?.LogInfo(CorePinDiagnostics.FormatLeftoverLine(leftover));
                }
                catch (Exception e)
                {
                    ProcessHost.Log?.LogWarning($"Core Harmony leftover scan: {e.Message}");
                }
                return true;
            }
            catch (Exception e)
            {
                try { HarmonySlots.RestoreHeld(); }
                catch (Exception restore) { ProcessHost.Log?.LogWarning($"HarmonySlots.RestoreHeld: {restore.Message}"); }
                error = e.ToString();
                return false;
            }
        }

        public void DropCoreWork()
        {
            string? ctx = _alc?.Name;
            var asm = Assembly;
            try { WorkQueue.DropOwnedBy(plugin: null, asm, ctx); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Core reload DropOwnedBy: {e.Message}"); }
        }

        public void NullRuntime()
        {
            ProcessHost.CoreTick = null;
            ProcessHost.SweepPlugin = null;
            ProcessHost.CoreAttached = false;
            ProcessHost.SetCoreLog(null);
            try { ProcessHost.SetLateFrame(null); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"NullRuntime SetLateFrame: {e.Message}"); }
            // Belt: Ui.Uninstall already unwired and nulled _core. A leftover
            // EventTrigger / UiInputContext.Update of this frame must not call
            // into Core after this point (client-hot-swap-crash.md).
            try { UiSlots.ClearAll(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"NullRuntime UiSlots.ClearAll: {e.Message}"); }
            // Job NATIVE1: the Action-field trampoline's Core dispatcher is the same class of
            // Bootstrap-held Core delegate as UiSlots' — clear it on the same paths.
            try { UiActionSlot.ClearAll(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"NullRuntime UiActionSlot.ClearAll: {e.Message}"); }
            try { UiInputContext.ClearKeyDispatch(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"NullRuntime ClearKeyDispatch: {e.Message}"); }
            // Belt for the same class of leftover, and load-bearing for collection (job FLATCH3):
            // UiMaskDetour's slot is the one field in that type that references collectible Core,
            // so a teardown that does not reach UninstallMask leaves a strong Bootstrap reference
            // into generation N and UnloadAlc does not collect.
            //
            // R47 finding 4 corrected the path this cites (job FLATCH4). It used to say "a Core
            // Uninstall that threw BEFORE Ui.Uninstall", and that case cannot get here at all:
            // Runtime.Uninstall logs and RETHROWS (Progenitor.Core/Runtime.cs), UninstallCore
            // returns false, and the cascade aborts at StepUninstall with RecoverPlugins — no
            // NullRuntime and no unload, because generation N is kept. The reachable path is a
            // throw INSIDE Ui.Uninstall, before it reaches UninstallMask: Plugin.Uninstall wraps
            // each module's teardown in its own try/catch (Progenitor.Core/Plugin.cs), so that
            // throw is swallowed, Uninstall reports success, and the cascade walks on to the
            // unload with the slot still filled. This clear is then the only cover, which is why
            // it is not decoration and must not be deleted as redundant.
            try { UiMaskDetour.ClearMask(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"NullRuntime UiMaskDetour.ClearMask: {e.Message}"); }
            try { UiInputContext.ClearCapture(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"NullRuntime ClearCapture: {e.Message}"); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public bool UnloadAlc(out bool collected, out string error)
        {
            error = "";
            collected = false;
            var alc = _alc;
            int gen = _generation;

            if (_alcUnloadRequested)
            {
                // Second Unload() throws InvalidOperationException and, if the ALC died
                // in the meantime, the old path left the process Core-less. Never call
                // Unload twice; treat an already-unloading ALC as did-not-collect unless
                // the weak is already dead (then Complete loads N+1). Never rehydrate
                // the old runtime — that pin is what collect is waiting to drop.
                error = "ALC.Unload already requested for this generation";
                collected = _previousAlc?.IsAlive != true;
                DropPublishedCore();
                LogAliveContexts();
                return false;
            }

            if (alc == null)
            {
                collected = true;
                DropPublishedCore();
                return true;
            }

            string name = alc.Name ?? CoreReloadPolicy.AlcNamePrefix;
            Action<string> log = m => ProcessHost.Log?.LogInfo(m);
            // Collect-start stack walk: must run before Unload so DeclaringType is still
            // valid, and from this Bootstrap method so the log proves no plugin/Core
            // frame is on the main thread.
            try { CoreReloadStackCheck.Log(Assembly, name, log); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Core pin-diag stack: {e.Message}"); }

            int leftover = 0;
            try { leftover = CorePinDiagnostics.CountCoreHarmonyLeftovers(Assembly); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Core Harmony leftover scan: {e.Message}"); }
            int inFlight = ReceiveDetours.InFlightCount;
            int alivePlugins = ReloaderHost.AliveCollectibleCount();
            if (!CoreReloadPolicy.MayUnloadCollectibleAlc(leftover, inFlight, alivePlugins))
            {
                error = leftover > 0
                    ? CorePinDiagnostics.FormatLeftoverLine(leftover)
                    : inFlight > 0
                        ? "receive in-flight=" + inFlight
                        : "plugin ALC still alive=" + alivePlugins;
                collected = false;
                // Keep generation N as the live ALC. Do not Unload: leftover JMPs would
                // jump into a freed trampoline. Do not drop the published assembly —
                // restore is ReinstallCurrent on this same object. Next RequestReload is
                // refused until restart.
                _previousAlc = CoreReloadPolicy.CreateGenerationWeak(alc);
                LogAliveContexts();
                return false;
            }

            try { CorePinDiagnostics.ReleaseCorePins(Assembly, name, leftover, log); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Core pin-release: {e.Message}"); }

            // Drop generation-N slot forwards immediately before Unload so they cannot
            // pin the ALC. Prefix snapshots (HarmonyPatches) still run After for the
            // nested Update. Do not release earlier: a leftover-Harmony refuse must
            // keep serving gen N until Restore re-Installs. Reload-path Install fills
            // the same slots as boot; they must be cleared here too (evidence 61).
            try { HarmonySlots.ReleaseHeld(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"HarmonySlots.ReleaseHeld: {e.Message}"); }
            try { ClearFailedInstallSlots(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"UnloadAlc slots: {e.Message}"); }
            try { HarmonyPatches.ClearAfterSnapshots(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"UnloadAlc AfterCall: {e.Message}"); }

            // Drop every strong ref to generation N before Unload. Restore-on-failure
            // reloads N's bytes into a fresh ALC (the collecting dry-run); it must not
            // keep this runtime / assembly / ALC alive across the collect poll.
            var asmForScan = Assembly;
            DropPublishedCore();
            try
            {
                CorePinDiagnostics.LogHolders(
                    asmForScan,
                    alc,
                    new object?[] { ReloadDriver, this, ProcessHost.Content },
                    log,
                    HolderScanWide);
            }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Core pin-diag holders: {e.Message}"); }
            WeakReference weak;
            try
            {
                // Job FLATCH4: the clear above already ran; handing it to the primitive is
                // what makes "every ALC unload clears every Core-referencing slot" a
                // property of the unload rather than of each caller. Idempotent by contract.
                weak = alc is CoreLoadContext coreAlc
                    ? CoreReloadCollect.PrepareAndUnload(coreAlc, ClearFailedInstallSlots)
                    : CoreReloadCollect.RequestUnload(alc, ClearFailedInstallSlots);
                LogClearError(name);
                _alcUnloadRequested = true;
            }
            catch (Exception e)
            {
                error = e.Message;
                weak = CoreReloadPolicy.CreateGenerationWeak(alc);
                collected = weak.IsAlive == false;
                _alcUnloadRequested = true;
                _previousAlc = weak;
                LogAliveContexts();
                // collected: TryBegin loads N+1. still alive: return true so the
                // driver polls, then restore via fresh LoadAndInstall — never put
                // `alc` back on fields (that pin is what collect waits on).
                return !collected;
            }

            // Do not wait here. Local `alc` still pins the context until this
            // method returns; the frame driver yields and polls `_previousAlc`.
            _previousAlc = weak;
            collected = false;
            ProcessHost.Log?.LogInfo(
                CoreReloadPolicy.StepLog(gen, "previous ALC " + name + " unload requested; collect poll starts next frame"));
            LogAliveContexts();
            return true;
        }

        /// <summary>
        /// Null every process-lifetime strong ref to the current Core generation.
        /// Leaves <c>_previousAlc</c> (weak) so the collect poll and MayStart still work.
        /// </summary>
        private static void DropPublishedCore()
        {
            Assembly = null;
            _alc = null;
            _generation = 0;
            _pendingAlcName = null;
        }

        public bool ReinstallCurrent(out string error)
        {
            error = "";
            var host = _host;
            if (host == null)
            {
                error = "Core host is missing.";
                return false;
            }
            if (Assembly == null)
            {
                error = "Core assembly is unpublished; restore must LoadAndInstall a fresh ALC.";
                return false;
            }
            try
            {
                if (!InvokeRuntime(CoreLayout.InstallMethod, args: new object?[] { host }, out error))
                    return false;
                ProcessHost.Log?.LogInfo(
                    CoreReloadPolicy.StepLog(_generation, "restored Install on " + (_alc?.Name ?? "alc")));
                return true;
            }
            catch (Exception e)
            {
                error = e.ToString();
                return false;
            }
        }

        public void UnretireCoreWork()
        {
            string? ctx = _alc?.Name;
            try { WorkQueue.UnretireNamed(ctx); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Core reload Unretire: {e.Message}"); }
        }

        public bool LoadAndInstall(byte[] bytes, int generation, out string error)
        {
            error = "";
            var host = _host;
            if (host == null)
            {
                error = "Core host is missing.";
                return false;
            }
            return LoadAndInstallBytes(bytes, generation, host, out error);
        }

        public void ClearSlotsAfterFailedInstall() => ClearFailedInstallSlots();

        public void ReleaseFailedGeneration(string? alcName) =>
            ReleaseFailedInstallGeneration(alcName);

        public void OpenLiveGate() => NotifyLiveWorldAfterCoreSwap();

        public void ReaskConnectedHandshake()
        {
            try { InvokeRuntime(CoreLayout.ReaskHandshakeMethod, args: null, out _); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"Core ReaskConnectedHandshake: {e.Message}"); }
        }

        public int ReloadPlugins()
        {
            int n = ReloaderHost.ReloadAll(evenIfLatched: true);
            try
            {
                InvokeRuntime(CoreLayout.ApplyPlaceholdersMethod, args: null, out _);
            }
            catch (Exception e)
            {
                ProcessHost.Log?.LogWarning($"Core ApplyKeyPlaceholdersAfterPlugins: {e.Message}");
            }
            return n;
        }

        public void Log(string message) => ProcessHost.Log?.LogInfo(message);

        private static void LogAliveContexts()
        {
            try
            {
                CorePinDiagnostics.LogCollectibleAlcs(m => ProcessHost.Log?.LogInfo(m));
            }
            catch (Exception e)
            {
                ProcessHost.Log?.LogWarning($"Core reload: listing ALCs threw: {e.Message}");
            }
        }
    }
}
