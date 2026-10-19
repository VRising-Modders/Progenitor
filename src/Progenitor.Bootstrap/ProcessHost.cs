using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace Progenitor.Bootstrap;

/// <summary>
/// Stable reflection contract Core uses to attach without a compile-time reference to this
/// assembly. Names and signatures here are the Step A/B/C seam; renaming them breaks Core's
/// <c>BootstrapLink</c>. Bootstrap must not reference Core: sweep, the per-frame Core tick,
/// and receive-detour slots are callbacks Core registers from <c>Runtime.Install</c>.
/// </summary>
public static class ProcessHost
{
    /// <summary>Simple name Core looks up on already-loaded assemblies.</summary>
    public const string AssemblySimpleName = "Progenitor.Bootstrap";

    /// <summary>Full type name Core passes to <c>Assembly.GetType</c>.</summary>
    public const string FullTypeName = "Progenitor.Bootstrap.ProcessHost";

    private static int _lastFrame = -1;
    private static bool _loggedArmError;
    private static ManualLogSource? _coreLog;

    /// <summary>
    /// Last Unity <c>Time.frameCount</c> observed by <see cref="Tick"/>. Used on
    /// Core-reload <c>unity-teardown frame=F</c> lines. -1 before the first tick.
    ///
    /// <para><b>It is "last observed", not "now"</b> (job FDOC5, R45 finding 34), and the
    /// difference matters to <see cref="AfterCallStack{T}"/>, which uses this as its clock.
    /// <see cref="Tick"/> is called by <c>FrameTicker.Update</c> and by <b>postfixes</b> on the
    /// two bootstrap systems' <c>OnUpdate</c>. If <c>FrameTicker.Install</c> threw — caught and
    /// logged in <c>Install</c>, the process carries on — the postfixes are the only callers left,
    /// and Harmony skips a postfix when the original throws. A bootstrap system that throws every
    /// frame therefore freezes this number, and every consumer that reads it as "the current
    /// rendered frame" is reading a frame that has stopped moving. Anything that must make
    /// progress in that state cannot be built on this counter alone.</para>
    /// </summary>
    public static int FrameCount => _lastFrame;

    /// <summary>
    /// True when Uninstall may <c>DestroyImmediate</c> UI roots. False means deferred
    /// <c>Object.Destroy</c> — the cascade is not yielding a frame before <c>ALC.Unload</c>.
    /// </summary>
    public static bool MayDestroyUiImmediate() => CoreReloadPolicy.MayDestroyImmediateOnUninstall;

    /// <summary>True after a successful <see cref="Install"/>; Core treats a missing or false value as fallback.</summary>
    public static bool IsInstalled { get; private set; }

    /// <summary>
    /// True after Bootstrap registered <c>Progenitor.Ticks.FrameTicker</c> with ClassInjector.
    /// Core must not pass its own <c>FrameTicker</c> type to ClassInjector (collectible
    /// MonoBehaviour, no unregister); it reads this flag instead.
    /// </summary>
    public static bool OwnsFrameTicker { get; internal set; }

    /// <summary>
    /// Bootstrap config <c>Diagnostics.SkipCoreHarmony</c>. When true, Core installs none of
    /// its Harmony patches (slots stay empty) so a dry-run Core reload can test the
    /// IL2CPP-trampoline pin hypothesis. Tick stays on Bootstrap. Refused on a live world
    /// unless <see cref="SkipCoreHarmonyAllowLiveWorld"/> is also true.
    /// </summary>
    public static bool SkipCoreHarmony { get; internal set; }

    /// <summary>
    /// Second confirm for <see cref="SkipCoreHarmony"/> against a save that already has a
    /// world (cloned GUIDs would drop). Default false.
    /// </summary>
    public static bool SkipCoreHarmonyAllowLiveWorld { get; internal set; }

    /// <summary>
    /// Bootstrap config <c>Diagnostics.HolderScanWide</c>. When true, a Core swap freeze
    /// walks statics of allowlisted non-collectible assemblies (BepInEx, Harmony,
    /// MonoMod, Il2CppInterop.Runtime, Bootstrap, BepInEx/plugins) plus Logger /
    /// Harmony / Il2CppInterop tables. Time-boxed at 400 ms. Default false. A
    /// dry-run reason of <c>dryRunWide</c> enables it for that run only.
    /// </summary>
    public static bool HolderScanWide { get; internal set; }

    /// <summary>
    /// Bootstrap config <c>Reload.WatchCore</c>. When true, a settled
    /// <c>Progenitor.dll.next</c> under <c>progenitor-core/</c> triggers the same
    /// cascade as the bridge <c>core_reload</c> op with reason <c>watch</c>.
    /// Ships <b>false</b> (<see cref="CoreWatchPolicy.DefaultWatchCore"/>, job FRLD):
    /// developers opt in, our own trees get the entry written by tools/deploy.ps1.
    /// </summary>
    public static bool WatchCore { get; internal set; } = CoreWatchPolicy.DefaultWatchCore;

    /// <summary>
    /// True when this process is the bundled server a client spawned for a private
    /// game (<c>-parentPID</c> / <c>progenitor-hosted</c>). Core file-watch is refused
    /// here so the child does not fight the client's swap.
    /// </summary>
    public static bool IsHostedChild { get; internal set; }

    /// <summary>
    /// True while this client has a live Progenitor hosted child
    /// (<c>-parentPID</c> / <c>progenitor-hosted</c> in <c>StunProcess.Start</c>
    /// arguments). WatchCore is refused so the child keeps the Core copied at
    /// spawn. Clears when that child exits or this client joins a dedicated
    /// server.
    /// </summary>
    public static bool IsHostingPrivateGame => HostedChild.Hosting;

    /// <summary>
    /// Job HOSTPROBE: identifies the hosted child once (off the main thread) and then only polls
    /// the handle it holds. See <see cref="HostedChildTracker"/>.
    /// </summary>
    internal static readonly HostedChildTracker HostedChild = new(
        HostedChildProbe.Instance,
        work => System.Threading.ThreadPool.UnsafeQueueUserWorkItem(_ => work(), null),
        Environment.ProcessId)
    {
        Warn = line => Log?.LogWarning(line),
    };

    /// <summary>
    /// Record a <c>StunProcess.Start</c> that looks like a Progenitor hosted child.
    /// Called from Bootstrap's process-lifetime prefix so Core does not have to.
    /// </summary>
    public static void NoteHostedSpawn(string? arguments) =>
        NoteHostedSpawn(fileName: null, arguments);

    /// <summary>
    /// Same as <see cref="NoteHostedSpawn(string?)"/> with the executable path
    /// from <c>StunProcess.Start</c>. Path is recorded so a later scan can match
    /// the child by path + argv, never by <c>VRisingServer.exe</c> name.
    /// </summary>
    public static void NoteHostedSpawn(string? fileName, string? arguments)
    {
        try
        {
            if (!CoreWatchPolicy.LooksLikeHostedSpawn(arguments)) return;
            HostedChild.NoteSpawn(fileName);
            _hostedSpawnAwaitingPid = true;
        }
        catch
        {
            // Prefix must not throw into StunProcess.Start.
        }
    }

    [ThreadStatic] private static bool _hostedSpawnAwaitingPid;

    /// <summary>
    /// True once after <see cref="NoteHostedSpawn(string?, string?)"/> recorded a hosted spawn on
    /// this thread, so the <c>StunProcess.Start</c> finalizer reads the child's PID only for that
    /// call and not for every process the game starts.
    /// </summary>
    internal static bool TakeHostedSpawnAwaitingPid()
    {
        bool was = _hostedSpawnAwaitingPid;
        _hostedSpawnAwaitingPid = false;
        return was;
    }

    /// <summary>
    /// Job HOSTPROBE: the PID <c>StunProcess.ProcessId</c> reported right after a hosted
    /// <c>Start</c>. A hint only; the background resolve still matches path + argv on the handle
    /// it opens. With it, no process scan runs at all.
    /// </summary>
    internal static void NoteHostedSpawnPid(int pid)
    {
        try { HostedChild.NoteSpawnPid(pid); }
        catch { /* finalizer must not throw into StunProcess.Start */ }
    }

    /// <summary>
    /// A <c>GameHelper.LaunchGame</c> that is a join, not a host, of a save that
    /// is not <c>progenitor-hosted</c>. Clears hosting so WatchCore resumes after
    /// the player quits a private game and connects to a dedicated server.
    /// </summary>
    public static void NoteDedicatedJoin(bool shouldHost, string? serverSaveName)
    {
        try
        {
            if (!CoreWatchPolicy.IsDedicatedJoin(shouldHost, serverSaveName)) return;
            ClearHostingPrivateGame(CoreWatchPolicy.ResumeReasonDedicatedJoin);
        }
        catch
        {
            // Prefix must not throw into LaunchGame.
        }
    }

    /// <summary>
    /// Job SERVERINST5. Bootstrap's published ability to record the joined server's address from
    /// <c>GameHelper.LaunchGame</c>. <c>0</c> (a Bootstrap that predates this, read through
    /// <c>BootstrapLink</c> as a missing member) is a valid pairing — Core then says so in the
    /// graceful-stop gate instead of pretending the port is simply wrong — so this is deliberately
    /// not a <c>CoreLayout.ProtocolVersion</c> bump, the same reasoning as
    /// <c>UiActionSlot.Capability</c>.
    /// </summary>
    public const int ConnectRecordCapability = 1;

    /// <summary>
    /// Job HOT2. Bootstrap's published ability to answer <see cref="CaptureAdmissionSnapshot"/>
    /// — the narrow loader-state snapshot the install lane's admission evaluator needs before it
    /// will admit a same-session activation. <c>0</c> (a Bootstrap that predates this, read through
    /// <c>BootstrapLink</c> as a missing member) is a valid pairing: Core's snapshot source then
    /// reports the capability as the unavailable reason, admission is <c>Unknown</c>, and the
    /// install takes the honest restart route. So this is deliberately not a
    /// <c>CoreLayout.ProtocolVersion</c> bump, the same reasoning as
    /// <see cref="ConnectRecordCapability"/> and <c>UiActionSlot.Capability</c>.
    /// </summary>
    public const int AdmissionSnapshotCapability = 1;

    /// <summary>
    /// Job HOT2: <see cref="AdmissionSnapshot.Capture(string,string,string)"/> under the host type
    /// Core already reflects onto, so the link needs one type lookup and not two. Plain values out;
    /// nothing about the returned object references a collectible assembly.
    ///
    /// <para>The return type is <see cref="object"/> because <see cref="AdmissionSnapshot"/> is
    /// internal: this is a Core/Bootstrap seam read by member name, not public API a plugin author
    /// calls. Core copies the values out through <c>BootstrapLink.TryCaptureAdmissionSnapshot</c>.</para>
    /// </summary>
    public static object CaptureAdmissionSnapshot(string? pluginId, string? fileName, string? sha256) =>
        AdmissionSnapshot.Capture(pluginId, fileName, sha256);

    /// <summary>Game port of the last <c>LaunchGame</c> join, or 0 when none was recorded.</summary>
    public static int LastConnectPort { get; private set; }

    /// <summary>Host of the last <c>LaunchGame</c> join, or "".</summary>
    public static string LastConnectAddress { get; private set; } = "";

    /// <summary>Transport of the last <c>LaunchGame</c> join (<c>ConnectAddress.Protocol</c>), or "".</summary>
    public static string LastConnectProtocol { get; private set; } = "";

    /// <summary>When <see cref="NoteConnect"/> last recorded a join. <c>default</c> when never.</summary>
    public static DateTime LastConnectUtc { get; private set; }

    /// <summary>
    /// Record the address a <c>GameHelper.LaunchGame</c> is joining, from Bootstrap's
    /// process-lifetime prefix. Last writer wins: the newest join is the one the client is on, and
    /// a rejoin must not be answered with the previous server's port. Kept here rather than in Core
    /// because Core is collectible and a reload must not forget which server this client joined.
    ///
    /// <para>Never throws: <see cref="LaunchGameHook"/>'s prefix runs inside the game's own call.</para>
    /// </summary>
    public static void NoteConnect(string? protocol, string? address, int port)
    {
        try
        {
            LastConnectProtocol = protocol ?? "";
            LastConnectAddress = address ?? "";
            LastConnectPort = port > 0 && port <= 65535 ? port : 0;
            LastConnectUtc = DateTime.UtcNow;
        }
        catch
        {
            // Prefix must not throw into LaunchGame.
        }
    }

    /// <summary>
    /// Drop <see cref="IsHostingPrivateGame"/> when the child with
    /// <c>-parentPID={this pid}</c> / <c>progenitor-hosted</c> has exited.
    /// Match by path + argv, never by process name. Job HOSTPROBE: main-thread cost is one
    /// non-blocking handle poll; identification runs on a background thread
    /// (<see cref="HostedChildTracker.Tick"/>).
    /// </summary>
    internal static void RefreshHostingPrivateGame()
    {
        if (IsHostedChild) return;
        // Not hosting: tick only to release a resolve result published after the flag was cleared (one volatile read).
        if (!HostedChild.Hosting)
        {
            if (!HostedChild.HasPublished) return;
            try { HostedChild.Tick(); }
            catch { /* best effort */ }
            return;
        }

        bool cleared;
        try { cleared = HostedChild.Tick(); }
        catch { return; }
        if (cleared) NoteHostingCleared(CoreWatchPolicy.ResumeReasonChildExited);
    }

    private static void ClearHostingPrivateGame(string reason)
    {
        if (!HostedChild.Clear()) return;
        NoteHostingCleared(reason);
    }

    private static void NoteHostingCleared(string reason)
    {
        try { CoreWatch.NoteHostingCleared(reason); }
        catch { /* resume log must not throw */ }
    }

    /// <summary>True after this process applied at least one Core Harmony patch on Bootstrap's id.</summary>
    public static bool OwnsHarmonyPatches => HarmonySlots.OwnsPatches;

    /// <summary>True when Bootstrap patched this original (slot names map onto the site).</summary>
    public static bool OwnsHarmonySite(string site) => HarmonySlots.OwnsSite(site);

    /// <summary>
    /// Core sets this at the end of <c>Runtime.Install</c> so plugin Scan cannot run before EventHooks
    /// and the rest of Core exist. Drain still runs so a bridge <c>Post</c> during Core.Install is not lost.
    /// Cleared for the window of a collectible Core swap so Scan cannot run against a half-loaded generation.
    /// </summary>
    public static bool CoreAttached { get; set; }

    /// <summary>
    /// True while <see cref="CoreTick"/> is running. <see cref="CoreLoader.RequestReload"/>
    /// always only schedules; a log line notes when that happened inside this callback.
    /// </summary>
    public static bool CoreTickInProgress { get; private set; }

    /// <summary>
    /// Process-lifetime clone / key-table / tombstone snapshot. Survives a collectible Core
    /// ALC swap; new Core copies it in on Install.
    /// </summary>
    public static ContentMemory Content { get; } = new();

    /// <summary>Unit despawns a Core generation could not finish, held for the next one.</summary>
    internal static DespawnHandoffBook Despawns { get; } = new();

    /// <summary>
    /// Core <c>Units.Uninstall</c>: keep unit despawns still in flight
    /// (<see cref="DespawnHandoffBook.Stride"/> ints per row) for the next generation. Optional
    /// for Core: a Bootstrap without this method leaves those units in the world, as before.
    /// </summary>
    public static int HandOffDespawns(int[] rows, ulong worldSequence) =>
        Despawns.HandOff(rows, worldSequence);

    /// <summary>
    /// Core <c>Units.Install</c>: take the rows handed off in the world with
    /// <paramref name="worldSequence"/> since the last save load. Every other row is dropped
    /// and counted in <paramref name="stale"/>.
    /// </summary>
    public static int[] TakeDespawns(ulong worldSequence, out int stale) =>
        Despawns.Take(worldSequence, out stale);

    /// <summary>Core's <c>Plugin.FrameTick</c>. Raised after Drain and (when attached) plugin Scan.</summary>
    public static Action? CoreTick { get; set; }

    private static Action? _lifetimeQuittingCore;
    private static Action? _lifetimeFrameCore;
    private static Action? _lateFrameCore;

    /// <summary>
    /// Collectible Core registers process-lifetime quit work here. Bootstrap's
    /// <c>Application.quitting</c> ConvertDelegate (default ALC) invokes
    /// <see cref="InvokeLifetimeQuitting"/> so Unity never holds a Core Action.
    /// </summary>
    public static Action? LifetimeQuitting
    {
        get => _lifetimeQuittingCore;
        set => SetLifetimeQuitting(value);
    }

    /// <summary>
    /// Collectible Core registers per-frame completion work here (hosted deferred Uninstall).
    /// Raised from <see cref="InvokeLifetimeFrame"/> on the Bootstrap ticker, not via a
    /// Core ConvertDelegate.
    /// </summary>
    public static Action? LifetimeFrame
    {
        get => _lifetimeFrameCore;
        set => SetLifetimeFrame(value);
    }

    /// <summary>
    /// Point the quit slot at a Core callback (or null it). A non-null callback is
    /// KeepDelegate'd under the delegate's Core ALC so <c>ReleaseGeneration</c> drops it.
    /// Nulling the field does not <c>DropByName</c>: Uninstall must leave the pin for
    /// the unload dump (evidence 57 hid <c>lifetime.quitting</c> from pin-release).
    /// The ConvertDelegate target stays <see cref="InvokeLifetimeQuitting"/>.
    /// </summary>
    public static void SetLifetimeQuitting(Action? callback)
    {
        _lifetimeQuittingCore = callback;
        if (callback != null)
            NativePinRegistry.KeepDelegate(callback, "lifetime.quitting", CoreLoader.CurrentPinGeneration);
    }

    /// <summary>Point the per-frame lifetime slot at a Core callback. See <see cref="SetLifetimeQuitting"/>.</summary>
    public static void SetLifetimeFrame(Action? callback)
    {
        _lifetimeFrameCore = callback;
        if (callback != null)
            NativePinRegistry.KeepDelegate(callback, "lifetime.frame", CoreLoader.CurrentPinGeneration);
    }

    /// <summary>
    /// Collectible Core's late-frame work (Effects hybrid SetPosition after the
    /// sequencer). Bootstrap's camera callback / FrameTicker LateUpdate invokes
    /// <see cref="InvokeLateFrame"/>; Unity never holds this Action. HDRP never
    /// fires <c>Camera.onPreCull</c>, so LateUpdate is the live drawing hook.
    /// </summary>
    public static Action? LateFrame
    {
        get => _lateFrameCore;
        set => SetLateFrame(value);
    }

    /// <summary>Point the late-frame slot at a Core callback (or null it). See <see cref="SetLifetimeQuitting"/>.</summary>
    public static void SetLateFrame(Action? callback)
    {
        _lateFrameCore = callback;
        if (callback != null)
            NativePinRegistry.KeepDelegate(callback, "lifetime.lateframe", CoreLoader.CurrentPinGeneration);
    }

    /// <summary>
    /// Bootstrap-owned invoker for <c>Application.quitting</c>. Must stay a method on this
    /// type so ConvertDelegate never captures a collectible Core Action.
    /// </summary>
    public static void InvokeLifetimeQuitting()
    {
        try { _lifetimeQuittingCore?.Invoke(); }
        catch (Exception e) { Log?.LogError($"Lifetime quitting slot threw: {e}"); }
    }

    /// <summary>Bootstrap-owned invoker for hosted deferred-Uninstall frame work.</summary>
    public static void InvokeLifetimeFrame()
    {
        try { _lifetimeFrameCore?.Invoke(); }
        catch (Exception e) { Log?.LogError($"Lifetime frame slot threw: {e}"); }
    }

    /// <summary>
    /// Bootstrap-owned invoker for the late-frame slot. Null Core is a no-op (empty-slot
    /// policy while a collectible generation is down).
    /// </summary>
    public static void InvokeLateFrame()
    {
        try { _lateFrameCore?.Invoke(); }
        catch (Exception e) { Log?.LogError($"LateFrame slot threw: {e}"); }
    }

    /// <summary>
    /// Core's owner-keyed registration sweep. Bootstrap never names EventBus / CommandRegistry /
    /// PrefabRegistry; those live in Core.
    /// </summary>
    public static Action<BasePlugin>? SweepPlugin { get; set; }

    internal static ManualLogSource? Log => _coreLog ?? Plugin.Logger;

    /// <summary>
    /// This plugin's <c>progenitor.cfg</c>, handed to Core. Core installs from inside
    /// <c>Plugin.Load</c>, before the chainloader has stored this plugin's instance, so Core's
    /// <c>IL2CPPChainloader.Plugins</c> lookup finds nothing at process start and only this
    /// property reaches the file then. Core reads it by reflection and treats null (an older
    /// Bootstrap) as "not yet". The file is process-lifetime; Core must bind only BCL types on it.
    /// </summary>
    public static BepInEx.Configuration.ConfigFile? SharedConfig { get; private set; }

    internal static void Install(BepInEx.Unity.IL2CPP.BasePlugin owner)
    {
        SharedConfig = owner?.Config;
        WorkQueue.CaptureMainThread();
        try { IsHostedChild = CoreWatchPolicy.IsHostedChild(Environment.CommandLine); }
        catch { IsHostedChild = false; }
        LogRing.Install();
        // Before anything else logs much: console lines go through a queue and a background writer from here on.
        string consoleLine = QueuedConsoleLogListener.Install(owner?.Config);
        if (consoleLine.Length > 0) Log?.LogInfo(consoleLine);
        SelfUpdate.Install();
        try
        {
            Ticks.FrameTicker.Install(owner);
            Log?.LogInfo("Frame ticker installed.");
        }
        catch (Exception e)
        {
            Log?.LogError($"Frame ticker failed; falling back to bootstrap-system ticks only: {e}");
        }

        try { ReceiveDetours.Install(); }
        catch (Exception e)
        {
            Log?.LogError($"Receive detours failed; Core will own them if it loads: {e}");
        }

        try { MaterialSeams.Install(); }
        catch (Exception e)
        {
            Log?.LogError($"materials: native seams failed to install; the game's own rules apply: {e}");
        }

        IsInstalled = true;

        var coreLog = BepInEx.Logging.Logger.CreateLogSource("Progenitor");
        SetCoreLog(coreLog);
        var host = new CoreHost(coreLog, Plugin.IsServerProcess, Content);
        if (!CoreLoader.TryLoadAndInstall(host))
        {
            // Missing-file and Install failures already logged. CoreAttached stays false so
            // plugin Scan does not run without EventHooks.
        }
    }

    /// <summary>
    /// Called from Bootstrap <c>Unload</c> after <c>CoreLoader.Uninstall</c>. Receive
    /// detours are process-lifetime: Uninstall clears the Core slot first and does not
    /// Dispose the JMP while the process can still receive (see
    /// <c>ReceiveDetours.Uninstall</c>).
    /// </summary>
    internal static void MarkUninstalled()
    {
        try { ReceiveDetours.Uninstall(); }
        catch (Exception e) { Log?.LogWarning($"ReceiveDetours.Uninstall: {e.Message}"); }
        // Same rule, same reason (job FLATCH3): null the Core slot, leave the JMP armed. This one
        // is on the game's per-frame input path, so disposing it here would be the use-after-free
        // UiMaskDetour exists to remove.
        try { UiMaskDetour.Uninstall(); }
        catch (Exception e) { Log?.LogWarning($"UiMaskDetour.Uninstall: {e.Message}"); }
        try { HarmonySlots.ClearAll(); }
        catch (Exception e) { Log?.LogWarning($"HarmonySlots.ClearAll: {e.Message}"); }
        try { IconCache.ClearAll(); }
        catch (Exception e) { Log?.LogWarning($"IconCache.ClearAll: {e.Message}"); }
        try { UiActionSlot.ClearAll(); }
        catch (Exception e) { Log?.LogWarning($"UiActionSlot.ClearAll: {e.Message}"); }
        IsInstalled = false;
        CoreAttached = false;
        CoreTick = null;
        SweepPlugin = null;
        SetLifetimeQuitting(null);
        SetLifetimeFrame(null);
        SetLateFrame(null);
        _coreLog = null;
        SharedConfig = null;
    }

    /// <summary>Core passes <c>Plugin.Logger</c> so reload lines keep source name <c>Progenitor</c>.</summary>
    public static void SetCoreLog(ManualLogSource? log) => _coreLog = log;

    /// <summary>
    /// One Unity frame. Order matches the split design: Drain, then an optional Core swap
    /// (never from inside Core's tick), then plugin Scan, then Core tick. De-dupes so the
    /// injected ticker and the bootstrap-system postfix cannot run work twice.
    /// </summary>
    public static void Tick()
    {
        int frame = UnityEngine.Time.frameCount;
        if (frame == _lastFrame) return;
        _lastFrame = frame;

        try { WorkQueue.Drain(); }
        catch (Exception e) { Log?.LogError($"MainThread drain threw: {e}"); }

        // Recovery lives above CoreAttached and runs even after a collectible plugin fails.
        try { ControlFormService.Tick(); }
        catch (Exception e) { Log?.LogError($"Form recovery tick: {e}"); }

        InvokeLifetimeFrame();

        // After Drain so a core_reload op has already returned (no Dev frame on
        // this stack) and before CoreTick so the cascade is never nested in Core.
        try { CoreLoader.TickReload(); }
        catch (Exception e) { Log?.LogError($"Core reload threw: {e}"); }

        try { CoreWatch.Tick(); }
        catch (Exception e) { Log?.LogWarning($"Core watch threw: {e.Message}"); }

        try { ReloaderHost.PollPluginLatch(); }
        catch (Exception e) { Log?.LogWarning("plugin-alc latch poll: " + e.Message); }

        // R31 finding 18: arm the process-exit signal above the CoreAttached gate below.
        // It used to hang off ReloaderHost.Tick, which a Core-less process never reaches
        // (MayScanPluginsWithoutCore is const false), so in exactly the process that most needs
        // to report Shutdown the handler was never subscribed at all.
        PluginUnloadSlot.Arm();
        if (PluginUnloadSlot.ArmError != null && !_loggedArmError)
        {
            _loggedArmError = true;
            Log?.LogWarning(PluginUnloadSlot.ArmError);
        }

        // Plugin ALC Resolving returns Core by simple name (Dev/Sample cannot
        // bind without it). CoreReloadPolicy.MayScanPluginsWithoutCore is false:
        // a Core-less process keeps serving CoreWatch so a matching image can
        // Install, then this Scan runs. The Dev bridge is a reload plugin.
        if (CoreAttached || CoreReloadPolicy.MayScanPluginsWithoutCore)
        {
            try { ReloaderHost.Tick(); }
            catch (Exception e) { Log?.LogError($"Reload scan failed: {e}"); }
        }

        CoreTickInProgress = true;
        try { CoreTick?.Invoke(); }
        catch (Exception e) { Log?.LogError($"Frame handler threw: {e}"); }
        finally { CoreTickInProgress = false; }
    }

    /// <summary>
    /// Queue a Core swap for a later <see cref="Tick"/> after Drain. Used when
    /// <see cref="CoreLoader.RequestReload"/> is invoked from inside <see cref="CoreTick"/>;
    /// RequestReload itself only schedules, so this is a compatibility wrapper.
    /// </summary>
    public static void ScheduleCoreReload(string reason) =>
        CoreLoader.RequestReload(string.IsNullOrEmpty(reason) ? "scheduled" : reason);

    public static bool IsMainThread => WorkQueue.IsMainThread;

    public static void Post(Action action, object? owner) => WorkQueue.Post(action, owner);

    public static Task<T> Run<T>(Func<T> func, object? owner) => WorkQueue.Run(func, owner);

    public static void PostDelayed(double seconds, Action action, object? owner) =>
        WorkQueue.PostDelayed(seconds, action, owner);

    public static void DropOwnedBy(object? plugin, Assembly? assembly, string? contextName) =>
        WorkQueue.DropOwnedBy(plugin, assembly, contextName);

    /// <summary>
    /// Duck-typed bind of <c>OnMainThreadDropped(Exception)</c> (Core's
    /// <c>IMainThreadDropTarget</c>). WorkQueue composes this into each item's OnDrop so a
    /// Dev <c>DeferredResult</c> / bridge gate completes when production-queue work is
    /// discarded. Bootstrap does not reference Core.
    /// </summary>
    internal static Action<Exception>? BindMainThreadDropTarget(object? owner)
    {
        if (owner == null) return null;
        MethodInfo? method;
        try
        {
            method = owner.GetType().GetMethod(
                "OnMainThreadDropped",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                binder: null,
                types: new[] { typeof(Exception) },
                modifiers: null);
        }
        catch
        {
            return null;
        }
        if (method == null) return null;
        return ex =>
        {
            try { method.Invoke(owner, new object[] { ex }); }
            catch (Exception e) { Log?.LogWarning($"MainThread drop target threw: {e.Message}"); }
        };
    }

    /// <summary>
    /// Reverse a Core-ALC <see cref="DropOwnedBy"/> so a restored generation can Post again.
    /// </summary>
    public static void UnretireContext(string? contextName) => WorkQueue.UnretireNamed(contextName);

    /// <summary>GCHandle / function-pointer table Core uses instead of allocating in its ALC.</summary>
    public static GCHandle AllocPin(object target, GCHandleType type, string name) =>
        NativePinRegistry.Alloc(target, type, name, CoreLoader.CurrentPinGeneration);

    public static IntPtr FunctionPointerFor(Delegate d, string name) =>
        NativePinRegistry.GetFunctionPointer(d, name, CoreLoader.CurrentPinGeneration);

    public static void KeepNativeDelegate(Delegate d, string name) =>
        NativePinRegistry.KeepDelegate(d, name, CoreLoader.CurrentPinGeneration);

    /// <summary>Free pins of the generation being unloaded. Never a global ReleaseAll on collect-fail.</summary>
    public static int ReleaseNativePins() => NativePinRegistry.ReleaseGeneration(CoreLoader.CurrentPinGeneration);

    public static int ReleaseNativePins(string? generation) => NativePinRegistry.ReleaseGeneration(generation);

    public static void Drain() => WorkQueue.Drain();

    public static long LatestSeq => LogRing.Instance.LatestSeq;

    /// <summary>
    /// The queued console's counters (<see cref="ConsoleLogQueue.ToArray"/>), or null when the console is not queued.
    /// Core's hitch summary reads it by name once per window; <paramref name="resetMaxima"/> starts the window's maxima again.
    /// An older Bootstrap has no such method and the summary leaves the field out.
    /// </summary>
    public static long[]? ConsoleLogCost(bool resetMaxima) => QueuedConsoleLogListener.Snapshot(resetMaxima);

    public static List<LogLine> ReadLogs(long since, int max, string? filter, int? minLevelValue)
    {
        LogLevel? min = minLevelValue.HasValue ? (LogLevel)minLevelValue.Value : null;
        var entries = LogRing.Instance.Read(since, max, filter, min);
        var lines = new List<LogLine>(entries.Count);
        for (int i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            lines.Add(new LogLine(e.Seq, e.Time, (int)e.Level, e.Source, e.Text));
        }
        return lines;
    }

    public static string ReloadFolder => ReloaderHost.Folder;

    public static List<string> RescanNow() => ReloaderHost.RescanNow();

    public static List<string> LoadedPluginNames() => ReloaderHost.LoadedPluginNames();

    /// <summary>
    /// Job FINJ1: reload-folder plugins that registered their own IL2CPP types (never unloaded in place),
    /// one <c>Il2CppInjectionPolicy.FormatRow</c> line each. Core's Mods tab reads it by name; an older
    /// Bootstrap has no such method and Core shows nothing.
    /// </summary>
    public static string[] ProcessLifetimePlugins() => ReloaderHost.ProcessLifetimeRows();

    /// <summary>
    /// Job FORD1: reload-folder DLLs waiting for a dependency (<c>PluginLoadOrder.FormatWaitingRow</c> lines).
    /// Core's Mods tab reads it by name; an older Bootstrap has no such method.
    /// </summary>
    public static string[] WaitingPlugins() => ReloaderHost.WaitingRows();

    public static bool TryGetSourcePath(Assembly asm, out string path) =>
        ReloaderHost.TryGetSourcePath(asm, out path);

    // ---- Per-server mod pause (reload-folder plugins only). Core binds these by name through
    // reflection and treats their absence as "this Bootstrap predates the pause" (no protocol
    // bump: an older Bootstrap degrades to the plain handshake refusal). Main thread only.

    /// <summary>
    /// Suspend the reload DLLs holding <paramref name="guids"/> (plus siblings and dependents).
    /// <paramref name="rowOwners"/> entries are <c>plugin\trow</c>. Returns the GUIDs now paused.
    /// </summary>
    public static string[] SuspendPlugins(string[] guids, string[] rowOwners, out string report) =>
        ReloaderHost.Suspend(guids, rowOwners, out report);

    /// <summary>
    /// End the pause of <paramref name="guids"/> (null: all) and load them. Returns only the GUIDs
    /// loaded now; the rest stay <c>resuming</c> in <see cref="SuspendedPlugins"/> until a scan loads them.
    /// </summary>
    public static string[] ResumePlugins(string[]? guids, out string report) =>
        ReloaderHost.Resume(guids, out report);

    /// <summary>Suspended plugins as <c>guid\tname\tversion\tstate\trows</c> rows.</summary>
    public static string[] SuspendedPlugins() => ReloaderHost.SuspendedPluginRows();

    /// <summary>
    /// A handshake request whose deferred reply a Core generation had not sent yet, carried across a
    /// Core swap so the next generation answers it. Opaque to Bootstrap; empty when none.
    /// </summary>
    public static string PendingHandshakeRequest { get; set; } = "";

    /// <summary>Keep still-resuming plugins paused. Returns the GUIDs kept.</summary>
    public static string[] KeepPluginsPaused(string[] guids) => ReloaderHost.KeepPaused(guids);

    /// <summary>
    /// Core's per-generation-surviving memory of resumed plugins it has not reported yet
    /// (<c>guid\treason\treanswer</c> lines). Opaque to Bootstrap.
    /// </summary>
    public static string PendingResumeReports { get; set; } = "";

    /// <summary>
    /// Identity of the connection the client is on: <see cref="LastConnectAddress"/>, port and the
    /// <see cref="LastConnectUtc"/> ticks of the <c>LaunchGame</c> that started it. Empty when none.
    /// </summary>
    public static string ConnectionIdentity =>
        LastConnectUtc == default ? "" : LastConnectAddress + ":" + LastConnectPort + "@" + LastConnectUtc.Ticks;

    /// <summary>GUIDs of loaded reload-folder plugins a suspend may take.</summary>
    public static string[] SuspendablePluginGuids() => ReloaderHost.SuspendableGuids();

    /// <summary>The GUIDs a suspend of <paramref name="guids"/> would take, or empty with a refusal.</summary>
    public static string[] PluginSuspendClosure(string[] guids, out string refusal) =>
        ReloaderHost.SuspendClosure(guids, out refusal);

    /// <summary>True after this process applied the client receive <c>INativeDetour</c>.</summary>
    public static bool OwnsClientReceive => ReceiveDetours.OwnsClient;

    /// <summary>True after this process applied the server receive <c>INativeDetour</c>.</summary>
    public static bool OwnsServerReceive => ReceiveDetours.OwnsServer;

    /// <summary>
    /// Core writes its client consume-or-pass dispatcher here (and nulls it on Uninstall).
    /// Returns false when Bootstrap did not apply the client detour, so Core may own it.
    /// </summary>
    public static bool TrySetClientReceiveSlot(Delegate? dispatcher) => ReceiveDetours.TrySetClient(dispatcher);

    /// <summary>
    /// Core writes its server consume-or-pass dispatcher here (and nulls it on Uninstall).
    /// Returns false when Bootstrap did not apply the server detour.
    /// </summary>
    public static bool TrySetServerReceiveSlot(Delegate? dispatcher) => ReceiveDetours.TrySetServer(dispatcher);

    /// <summary>
    /// Core binds its four material seam handlers (count gate, count compose, pay gate, pay), or clears
    /// them with four nulls. False when this process did not arm the opt-in seams it needs.
    /// </summary>
    public static bool TrySetMaterialSlots(Delegate? countGate, Delegate? compose, Delegate? payGate, Delegate? pay) =>
        MaterialSeams.TrySet(countGate, compose, payGate, pay);

    /// <summary>The <c>materials:</c> state line: enabled or not, and what each seam did at install.</summary>
    public static string MaterialSeamsDescribe() => MaterialSeams.Describe();

    /// <summary>
    /// Record an event id Core (or a plugin through Core) owns. Process-lifetime: Core
    /// Unload must still drop this fourcc. Call before pointing the dispatcher.
    /// </summary>
    public static void RegisterOwnedEventId(int eventId) => ReceiveSlotPolicy.RegisterOwnedEventId(eventId);

    /// <summary>
    /// Core writes one Harmony handler into a process-lifetime slot (and nulls it on
    /// Uninstall). Returns false when Bootstrap did not apply the patches, so Core may
    /// patch itself as a fallback.
    /// </summary>
    public static bool TrySetHarmonySlot(string site, Delegate? dispatcher) =>
        HarmonySlots.TrySet(site, dispatcher);

    /// <summary>
    /// Plugin / extra-instance bind with an owner token. A second bind by a
    /// different owner is refused. Do not add an overload of
    /// <see cref="TrySetHarmonySlot"/>: Core reflects this name with two
    /// arguments and an extra overload is <c>AmbiguousMatchException</c>.
    /// </summary>
    public static bool TryBindPluginSlot(string site, Delegate dispatcher, string owner) =>
        HarmonySlots.TrySet(site, dispatcher, owner);

    /// <summary>Null one plugin slot when <paramref name="owner"/> matches (or is empty: force).</summary>
    public static bool TryClearPluginSlot(string site, string? owner) =>
        HarmonySlots.TrySet(site, null, owner);

    /// <summary>Null every plugin slot bound by <paramref name="owner"/>.</summary>
    public static void ClearOwnedPluginSlots(string owner) =>
        HarmonySlots.ClearOwned(owner);

    /// <summary>
    /// Null plugin slots whose dispatcher lives in <paramref name="assembly"/> /
    /// <paramref name="alcName"/>, then any remaining slots owned by those names.
    /// ReloaderHost calls this on plugin Unload so a missed <c>ClearPlugin</c>
    /// cannot pin the ALC.
    /// </summary>
    public static void ClearPluginSlotsForUnload(Assembly? assembly, string? alcName) =>
        HarmonySlots.ClearPluginContext(assembly, alcName);

    /// <summary>Null one Harmony slot. No-op when Bootstrap did not apply the patches.</summary>
    public static void ClearHarmonySlot(string site) => HarmonySlots.TrySet(site, null);

    /// <summary>Null every Harmony slot. Safe to call twice.</summary>
    public static void ClearAllHarmonySlots() => HarmonySlots.ClearAll();

    /// <summary>
    /// Process-lifetime Sprite cache. Core reflects these names; do not add an overload
    /// of <see cref="TryGetOrCreateSprite"/> (AmbiguousMatchException). The Sprite and
    /// its Texture2D are one cache entry; Core must not <c>Destroy</c> them.
    /// </summary>
    public static bool TryGetOrCreateSprite(string key, byte[] bytes, out object? sprite)
    {
        sprite = null;
        try
        {
            if (!IconCache.TryGetOrCreate(key, bytes, out var created) || created == null)
                return false;
            sprite = created;
            return true;
        }
        catch (Exception e)
        {
            Log?.LogWarning("TryGetOrCreateSprite: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Lookup by plugin id + relative path when PNG bytes are not on disk (Core swap
    /// re-apply before the plugin file is in the reloader <c>ByPath</c>).
    /// </summary>
    public static bool TryGetCachedSprite(string pluginId, string relativePath, out object? sprite)
    {
        sprite = null;
        try
        {
            if (!IconCache.TryGet(pluginId, relativePath, out var found) || found == null)
                return false;
            sprite = found;
            return true;
        }
        catch (Exception e)
        {
            Log?.LogWarning("TryGetCachedSprite: " + e.Message);
            return false;
        }
    }

    /// <summary>Destroy one cached Sprite+Texture2D pair. No-op when the key is missing.</summary>
    public static bool ReleaseSprite(string key)
    {
        try { return IconCache.Release(key); }
        catch (Exception e)
        {
            Log?.LogWarning("ReleaseSprite: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Destroy every cached pair for this plugin. ReloaderHost calls this when the
    /// reload-folder file is removed, not on a Core-swap Unload.
    /// </summary>
    public static int ReleasePluginSprites(string pluginId)
    {
        try { return IconCache.ReleasePlugin(pluginId); }
        catch (Exception e)
        {
            Log?.LogWarning("ReleasePluginSprites: " + e.Message);
            return 0;
        }
    }
}

/// <summary>DTO so Core can map Bootstrap log rows onto <c>Progenitor.Dev.LogBuffer.Entry</c> without sharing a type.</summary>
public readonly struct LogLine
{
    public LogLine(long seq, DateTime time, int level, string source, string text)
    {
        Seq = seq;
        Time = time;
        Level = level;
        Source = source;
        Text = text;
    }

    public long Seq { get; }
    public DateTime Time { get; }
    public int Level { get; }
    public string Source { get; }
    public string Text { get; }
}
