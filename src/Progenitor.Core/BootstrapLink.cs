using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Progenitor.Ticks;

namespace Progenitor;

/// <summary>
/// Reflection seam onto <c>Progenitor.Bootstrap.ProcessHost</c>. Core must not compile-reference
/// Bootstrap: a missing <c>Progenitor.Bootstrap.dll</c> has to fall back to Core-owned
/// process-lifetime services. Looks only at already-loaded assemblies so a missing file is not
/// probed from disk. Step D: Bootstrap <c>LoadFromStream</c>s Core into a collectible ALC, so
/// this attach succeeds on the production path; the fallback remains for tests and a Core that
/// loaded some other way.
/// </summary>
internal static class BootstrapLink
{
    internal const string AbsentLog = "Bootstrap absent: Core owns process-lifetime services";

    /// <summary>
    /// Bootstrap ↔ Core install handshake. Must equal
    /// <c>Progenitor.Bootstrap.CoreLayout.ProtocolVersion</c>. Bump both in the same
    /// change whenever Install/Uninstall/<c>ICoreHost</c> shape, Harmony/receive slot
    /// names, or ContentMemory layout change. A mismatch refuses the load with both
    /// numbers in the log and restores last-good. See docs/modules.md Hot reload.
    ///
    /// 3 (R31 finding 13): the plugin unload hand-off slot
    /// (<c>Progenitor.Bootstrap.PluginUnloadSlot</c>, read here by
    /// <c>Progenitor.Plugins.BootstrapUnloadSlotProbe</c>) joined the handshake. An older
    /// Bootstrap never writes it, so this Core would infer Unknown on every unload and never
    /// sweep a deleted plugin; refusing is the honest answer, and the mismatch path already logs
    /// both numbers and stays Core-less.
    ///
    /// 4 (job FHOOK3): the four system-update slot delegates widened from <c>bool hasAfter</c> to
    /// <c>long callToken</c> so <c>SystemHooks.PostfixManaged</c> releases the call frame this call
    /// pushed instead of whatever is on top of <c>HookCallStack</c>. The declarations exist once in
    /// <c>Progenitor.Core.Hooks.SystemHooks</c> and once in
    /// <c>Progenitor.Bootstrap.HarmonySlots</c> and are joined by <c>Delegate.CreateDelegate</c>,
    /// so a mismatched pair would bind a skewed delegate, throw in <c>Bind</c>, and take both
    /// system-hook sites Inert - system hooks silently off for the generation. Refusing the load
    /// is what makes that widening safe.
    ///
    /// <b>5</b> (2026-10 id rename): Bootstrap's BepInEx GUID became <c>progenitor</c> and its Harmony id
    /// <c>progenitor.bootstrap</c>, which Core reads by name (<c>BootstrapLink</c>); consumer plugins now declare
    /// <c>[BepInDependency("progenitor")]</c>, which an older Bootstrap does not satisfy, and Bootstrap renames the
    /// old cfg files before anything binds them. A new Core under an older Bootstrap would bind fresh, empty cfg
    /// files beside the old ones, so the pairing is refused and the process stays Core-less until Bootstrap is
    /// deployed and the process restarted.
    /// </summary>
    public const int ProtocolVersion = 5;

    private const string AssemblySimpleName = "Progenitor.Bootstrap";
    private const string HostTypeName = "Progenitor.Bootstrap.ProcessHost";
    private const string BootstrapHarmonyId = "progenitor.bootstrap";

    public static bool OwnsLifetime { get; private set; }

    public static IMainThreadBackend? MainThread { get; private set; }
    public static ILogBackend? Logs { get; private set; }
    public static IReloaderBackend? Reloader { get; private set; }
    public static IReceiveSlotBackend? ReceiveSlots { get; private set; }
    public static IHarmonySlotBackend? HarmonySlots { get; private set; }

    /// <summary>
    /// Point Bootstrap's process-lifetime <c>Application.quitting</c> slot at a Core callback.
    /// The ConvertDelegate lives in Bootstrap; this is an ordinary managed Action.
    /// </summary>
    /// <summary>
    /// Allocate a GCHandle in Bootstrap so collectible Core never holds one. Freed on
    /// Core Uninstall via Bootstrap's pin registry.
    /// </summary>
    public static bool TryAllocPin(object target, System.Runtime.InteropServices.GCHandleType type, string name, out System.Runtime.InteropServices.GCHandle handle)
    {
        handle = default;
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return false;
            var mi = host.GetMethod("AllocPin", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            var result = mi.Invoke(null, new object?[] { target, type, name });
            if (result is System.Runtime.InteropServices.GCHandle h)
            {
                handle = h;
                return true;
            }
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Bootstrap AllocPin: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// Hand unit despawns still in flight to Bootstrap (<c>ProcessHost.HandOffDespawns</c>): three
    /// ints per row (index, version, PrefabGUID hash). Returns the rows Bootstrap accepted, or -1
    /// when there is no Bootstrap or it predates the hand-off.
    /// </summary>
    public static int TryHandOffDespawns(int[] rows, ulong worldSequence)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("HandOffDespawns", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return -1;
            return mi.Invoke(null, new object?[] { rows, worldSequence }) is int n ? n : -1;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Bootstrap HandOffDespawns: {e.Message}");
            return -1;
        }
    }

    /// <summary>
    /// Take the unit despawns an earlier Core generation handed to Bootstrap in the world with
    /// <paramref name="worldSequence"/> (<c>ProcessHost.TakeDespawns</c>). Null when there is no
    /// Bootstrap or it predates the hand-off; <paramref name="stale"/> counts rows Bootstrap
    /// dropped because their world was replaced or reloaded.
    /// </summary>
    public static int[]? TryTakeDespawns(ulong worldSequence, out int stale)
    {
        stale = 0;
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("TakeDespawns", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return null;
            var args = new object?[] { worldSequence, 0 };
            var rows = mi.Invoke(null, args) as int[];
            if (args[1] is int s) stale = s;
            return rows;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Bootstrap TakeDespawns: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// <c>Marshal.GetFunctionPointerForDelegate</c> routed through Bootstrap. The pointer is
    /// invalid after Core Uninstall (registry ReleaseAll).
    /// </summary>
    public static bool TryFunctionPointer(Delegate d, string name, out IntPtr pointer)
    {
        pointer = IntPtr.Zero;
        if (d == null) throw new ArgumentNullException(nameof(d));
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return false;
            var mi = host.GetMethod("FunctionPointerFor", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            var result = mi.Invoke(null, new object?[] { d, name });
            if (result is IntPtr p)
            {
                pointer = p;
                return true;
            }
            return false;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Bootstrap FunctionPointerFor: {e.Message}");
            return false;
        }
    }

    public static void SetLifetimeQuitting(Action? callback) =>
        TrySetLifetime("SetLifetimeQuitting", callback);

    /// <summary>Point Bootstrap's per-frame lifetime slot at a Core callback. See <see cref="SetLifetimeQuitting"/>.</summary>
    public static void SetLifetimeFrame(Action? callback) =>
        TrySetLifetime("SetLifetimeFrame", callback);

    /// <summary>
    /// Point Bootstrap's late-frame slot at a Core callback. Raised from
    /// <c>Camera.onPreCull</c> / <c>onPreRender</c>, or FrameTicker LateUpdate
    /// when those never fire (HDRP). Unity never holds this Action.
    /// </summary>
    public static void SetLateFrame(Action? callback) =>
        TrySetLifetime("SetLateFrame", callback);

    private static void TrySetLifetime(string method, Action? callback)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return;
            var mi = host.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (mi == null)
            {
                var prop = host.GetProperty(method.StartsWith("Set", StringComparison.Ordinal) ? method.Substring(3) : method, BindingFlags.Public | BindingFlags.Static);
                prop?.SetValue(null, callback);
                return;
            }
            mi.Invoke(null, new object?[] { callback });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Bootstrap {method}: {e.Message}");
        }
    }

    private static Type? _hostType;

    /// <summary>
    /// Bootstrap's <c>ProcessHost</c> type once linked, else null. Per-server mod pause binds its
    /// optional entry points from here with a capability check (<c>ReflectedPauseBackend</c>).
    /// </summary>
    internal static Type? HostType => _hostType;
    private static bool _coreQueueFallback;

    /// <summary>
    /// Attach to a Bootstrap that has already <c>Load</c>ed. Returns true when Bootstrap owns
    /// the process-lifetime tick (installed, FrameTicker FullName already injected, or Harmony
    /// postfix already on <c>OnUpdate</c>) so Core must not install a second ticker. Returns
    /// false when the assembly is missing or Bootstrap never took the tick, which is Core's
    /// fallback. Does not register Core callbacks; call <see cref="NotifyCoreReady"/> after
    /// Core modules are installed.
    /// </summary>
    public static bool TryAttach()
    {
        EnsureBootstrapProtocol();
        Type? hostType = null;
        try { hostType = FindHostType(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"Bootstrap FindHostType: {e.Message}"); }

        bool tickerRegistered = ProbeTickerRegistered();
        bool harmonyLive = ProbeBootstrapHarmony();
        bool installed = false;
        if (hostType != null)
        {
            try { installed = IsInstalled(hostType); }
            catch (Exception e) { Plugin.Logger?.LogWarning($"Bootstrap IsInstalled: {e.Message}"); }
        }

        bool tickLive = Networking.ReceiveSlotHandoff.AttachOwnsTick(installed, tickerRegistered, harmonyLive);

        if (hostType == null)
        {
            // ProcessHost missing/renamed is not "Bootstrap absent": still refuse a
            // second OnUpdate postfix when the ticker or Harmony id is already live.
            if (tickLive)
            {
                StayInertAfterFailedAttach(null);
                _coreQueueFallback = true;
                OwnsLifetime = true;
                Plugin.Logger?.LogError("Bootstrap assembly is loaded but ProcessHost was not found; Core stays inert for ticks (no second OnUpdate postfix). MainThread drained once; later posts need a host adapter.");
                return true;
            }
            OwnsLifetime = false;
            _coreQueueFallback = false;
            return false;
        }

        try
        {
            if (!tickLive)
            {
                // Bootstrap.dll loaded but did not take the tick. Core owns the fallback
                // ticker; keep Bootstrap's log ring if it is already a listener. Keep the
                // receive-slot adapter so Core can still see OwnsClient/OwnsServer.
                TryWireLogs(hostType, onlyIfRingPresent: true);
                TryWireReceiveSlots(hostType);
                TryWireHarmonySlots(hostType);
                OwnsLifetime = false;
                _coreQueueFallback = false;
                _hostType = hostType;
                return false;
            }

            WireAdapters(hostType);
            _hostType = hostType;
            if (Logs != null)
                Dev.LogBuffer.Backend = Logs;

            _coreQueueFallback = Networking.ReceiveSlotHandoff.MustDrainCoreQueue(MainThread != null);
            if (MainThread != null)
            {
                try { Dev.MainThread.HandoffPending(MainThread.Post, MainThread.PostDelayed); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"MainThread handoff failed: {e.Message}");
                    _coreQueueFallback = true;
                    MainThread = null;
                }
            }
            else
            {
                StayInertAfterFailedAttach(null);
                _coreQueueFallback = true;
            }

            OwnsLifetime = true;
            return true;
        }
        catch (Exception e)
        {
            _hostType = hostType;
            TryWireLogs(hostType, onlyIfRingPresent: true);
            TryWireReceiveSlots(hostType);
            TryWireHarmonySlots(hostType);
            _coreQueueFallback = Networking.ReceiveSlotHandoff.MustDrainCoreQueue(MainThread != null);
            if (tickLive)
            {
                StayInertAfterFailedAttach(e);
                OwnsLifetime = true;
                return true;
            }

            Plugin.Logger?.LogWarning($"Bootstrap attach failed; Core will own process-lifetime services: {e.Message}");
            OwnsLifetime = false;
            MainThread = null;
            Reloader = null;
            return false;
        }
    }

    /// <summary>
    /// Point Bootstrap at Core's tick, sweep, and logger, then allow plugin Scan. Call at the
    /// end of <c>Plugin.Install</c> so reloadable plugins cannot construct before EventHooks exist.
    /// When attach was inert (ticker/Harmony already live) this still publishes CoreTick so
    /// a missing MainThread adapter drains Core's leftover queue from ProcessHost.Tick.
    /// </summary>
    public static void NotifyCoreReady()
    {
        if (!OwnsLifetime || _hostType == null) return;
        try
        {
            var setLog = _hostType.GetMethod("SetCoreLog", BindingFlags.Public | BindingFlags.Static);
            setLog?.Invoke(null, new object?[] { Plugin.Logger });
            Action tick = _coreQueueFallback ? DrainCoreQueueThenTick : FrameTickAdapter;
            SetStatic(_hostType, "CoreTick", tick);
            SetStatic(_hostType, "SweepPlugin", (Action<BasePlugin>)Reload.PluginReloader.SweepRegistrations);
            SetStatic(_hostType, "CoreAttached", true);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Bootstrap NotifyCoreReady failed: {e}");
        }
    }

    private static void FrameTickAdapter() => Plugin.FrameTick();

    private static void DrainCoreQueueThenTick()
    {
        try { Dev.MainThread.Drain(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"MainThread fallback drain threw: {e.Message}"); }
        Plugin.FrameTick();
    }

    private static Type? FindBootstrapType(string fullName)
    {
        var host = _hostType ?? FindHostType();
        if (host?.Assembly != null)
        {
            try
            {
                var t = host.Assembly.GetType(fullName);
                if (t != null) return t;
            }
            catch { /* */ }
        }

        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, AssemblySimpleName, StringComparison.Ordinal))
                continue;
            try { return asm.GetType(fullName); }
            catch { return null; }
        }

        return null;
    }

    private static bool InvokeUiSlotsBool(string method, params object?[] args)
    {
        try
        {
            var type = FindBootstrapType("Progenitor.Bootstrap.UiSlots");
            var mi = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            return mi.Invoke(null, args) is true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UiSlots." + method + ": " + e.Message);
            return false;
        }
    }

    private static void InvokeUiSlots(string method, params object?[] args)
    {
        try
        {
            var type = FindBootstrapType("Progenitor.Bootstrap.UiSlots");
            var mi = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            mi?.Invoke(null, args.Length == 0 ? Array.Empty<object>() : args);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UiSlots." + method + ": " + e.Message);
        }
    }

    private static bool InvokeUiMaskBool(string method, params object?[] args)
    {
        try
        {
            var type = FindBootstrapType("Progenitor.Bootstrap.UiMaskDetour");
            var mi = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            return mi.Invoke(null, args) is true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UiMaskDetour." + method + ": " + e.Message);
            return false;
        }
    }

    private static void InvokeUiMask(string method, params object?[] args)
    {
        try
        {
            var type = FindBootstrapType("Progenitor.Bootstrap.UiMaskDetour");
            var mi = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            mi?.Invoke(null, args.Length == 0 ? Array.Empty<object>() : args);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UiMaskDetour." + method + ": " + e.Message);
        }
    }

    private static bool InvokeUiInputBool(string method, params object?[] args)
    {
        try
        {
            var type = FindBootstrapType("Progenitor.Ui.UiInputContext");
            var mi = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            return mi.Invoke(null, args) is true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UiInputContext." + method + ": " + e.Message);
            return false;
        }
    }

    private static void InvokeUiInput(string method, params object?[] args)
    {
        try
        {
            var type = FindBootstrapType("Progenitor.Ui.UiInputContext");
            var mi = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            mi?.Invoke(null, args.Length == 0 ? Array.Empty<object>() : args);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UiInputContext." + method + ": " + e.Message);
        }
    }

    private static Type? FindHostType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, AssemblySimpleName, StringComparison.Ordinal))
                continue;
            var named = asm.GetType(HostTypeName);
            if (named != null) return named;
            try
            {
                foreach (var t in asm.GetExportedTypes())
                {
                    if (t.GetProperty("OwnsClientReceive", BindingFlags.Public | BindingFlags.Static) == null)
                        continue;
                    if (t.GetMethod("Post", BindingFlags.Public | BindingFlags.Static) == null)
                        continue;
                    return t;
                }
            }
            catch
            {
                // GetExportedTypes can throw on a ReflectionTypeLoadException; treat as missing.
            }
            return null;
        }
        return null;
    }

    private static bool IsInstalled(Type hostType)
    {
        var prop = hostType.GetProperty("IsInstalled", BindingFlags.Public | BindingFlags.Static);
        return prop?.GetValue(null) is true;
    }

    private static bool ProbeTickerRegistered()
    {
        try
        {
            // Do not pass Core's FrameTicker type to ClassInjector: that type lives in
            // the collectible ALC, and IsTypeRegisteredInIl2Cpp(typeof(T)) touches
            // Il2CppClassPointerStore<T> for Core's T. Bootstrap exposes OwnsFrameTicker
            // after it registers the process-lifetime type of the same FullName.
            if (ReadHostBool("OwnsFrameTicker")) return true;
            var host = _hostType ?? FindHostType();
            if (host == null) return false;
            return host.GetProperty("OwnsFrameTicker", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true
                || IsInstalled(host);
        }
        catch
        {
            // Fail closed: a throw is not "not registered". Treating it as free would
            // postfix OnUpdate on top of Bootstrap's ticker (R10 finding 9).
            return true;
        }
    }

    private static bool ProbeBootstrapHarmony()
    {
        try
        {
            // Reflection so this file has no HarmonyLib TypeRef (collectible production path).
            var harmonyType = Type.GetType("HarmonyLib.Harmony, 0Harmony", throwOnError: false);
            var hasAny = harmonyType?.GetMethod("HasAnyPatches", BindingFlags.Public | BindingFlags.Static);
            if (hasAny == null) return false;
            return hasAny.Invoke(null, new object[] { BootstrapHarmonyId }) is true;
        }
        catch
        {
            return false;
        }
    }

    private static void WireAdapters(Type hostType)
    {
        try { MainThread = new MainThreadAdapter(hostType); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Bootstrap MainThread adapter failed: {e.Message}");
            MainThread = null;
        }

        TryWireLogs(hostType, onlyIfRingPresent: false);

        try { Reloader = new ReloaderAdapter(hostType); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Bootstrap reloader adapter failed: {e.Message}");
            Reloader = null;
        }

        TryWireReceiveSlots(hostType);
        TryWireHarmonySlots(hostType);
    }

    private static void TryWireReceiveSlots(Type hostType)
    {
        try { ReceiveSlots = new ReceiveSlotAdapter(hostType); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Bootstrap receive-slot adapter failed: {e.Message}");
            // Do not null a previously wired adapter. Nulling it on attach-fail is what
            // let Core CreateAndApply on an already-hooked function (R10 finding 1).
        }
    }

    private static void TryWireHarmonySlots(Type hostType)
    {
        try { HarmonySlots = new HarmonySlotAdapter(hostType); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Bootstrap Harmony-slot adapter failed: {e.Message}");
        }
    }

    private static void StayInertAfterFailedAttach(Exception? cause)
    {
        if (cause == null)
            Plugin.Logger?.LogError(TickOwnership.AttachFailedInertLog);
        else
            Plugin.Logger?.LogError($"{TickOwnership.AttachFailedInertLog}: {cause.Message}");
        try { Dev.MainThread.Drain(); }
        catch (Exception e) { Plugin.Logger?.LogWarning($"MainThread drain during inert attach failed: {e.Message}"); }
    }

    private static void TryWireLogs(Type? hostType, bool onlyIfRingPresent)
    {
        if (hostType == null) return;
        if (onlyIfRingPresent && !Dev.LogBuffer.BootstrapRingIsListening()) return;
        try
        {
            Logs = new LogAdapter(hostType);
            Dev.LogBuffer.Backend = Logs;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Bootstrap log adapter failed: {e.Message}");
            if (Dev.LogBuffer.Backend == Logs)
                Dev.LogBuffer.Backend = null;
            Logs = null;
        }
    }

    private static void SetStatic(Type type, string name, object? value)
    {
        var prop = type.GetProperty(name, BindingFlags.Public | BindingFlags.Static);
        if (prop != null)
        {
            prop.SetValue(null, value);
            return;
        }
        var field = type.GetField(name, BindingFlags.Public | BindingFlags.Static);
        field?.SetValue(null, value);
    }

    private static object Invoke(Type type, string name, params object?[] args)
    {
        var method = type.GetMethod(name, BindingFlags.Public | BindingFlags.Static)
            ?? throw new MissingMethodException(type.FullName, name);
        try
        {
            return method.Invoke(null, args)!;
        }
        catch (TargetInvocationException e) when (e.InnerException != null)
        {
            throw e.InnerException;
        }
    }

    internal interface IMainThreadBackend
    {
        bool IsMainThread { get; }
        void Post(Action action, object? owner);
        Task<T> Run<T>(Func<T> func, object? owner);
        void PostDelayed(double seconds, Action action, object? owner);
        void DropOwnedBy(object? plugin, Assembly? assembly, string? contextName);
        void Drain();
    }

    internal interface ILogBackend
    {
        long LatestSeq { get; }
        List<Dev.LogBuffer.Entry> Read(long since, int max, string? filter, LogLevel? minLevel);
    }

    internal interface IReloaderBackend
    {
        string Folder { get; }
        List<string> RescanNow();
        List<string> LoadedPluginNames();
        bool TryGetSourcePath(Assembly asm, out string path);
    }

    internal interface IReceiveSlotBackend
    {
        bool OwnsClient { get; }
        bool OwnsServer { get; }
        bool TrySetClient(Delegate dispatcher);
        bool TrySetServer(Delegate dispatcher);
        void ClearClient();
        void ClearServer();
        void RegisterOwnedEventId(int eventId);
    }

    internal interface IHarmonySlotBackend
    {
        bool OwnsPatches { get; }
        bool SkipCoreHarmony { get; }
        bool OwnsSite(string site);
        bool TrySet(string site, Delegate dispatcher);
        void Clear(string site);
        void ClearAll();
    }

    /// <summary>
    /// True when Bootstrap applied the client receive detour in this process. Reads the
    /// host type even when the adapter is null so an inert attach cannot flip this off
    /// and let Core <c>CreateAndApply</c> on the same address.
    /// </summary>
    public static bool OwnsClientReceive =>
        ReceiveSlots?.OwnsClient == true || ReadHostBool("OwnsClientReceive");

    /// <summary>True when Bootstrap applied the server receive detour in this process.</summary>
    public static bool OwnsServerReceive =>
        ReceiveSlots?.OwnsServer == true || ReadHostBool("OwnsServerReceive");

    /// <summary>True when Bootstrap applied at least one Core Harmony patch in this process.</summary>
    public static bool OwnsHarmonyPatches =>
        HarmonySlots?.OwnsPatches == true || ReadHostBool("OwnsHarmonyPatches");

    /// <summary>True when Bootstrap patched this original. Slot names map onto the site.</summary>
    public static bool OwnsHarmonySite(string site)
    {
        if (HarmonySlots != null) return HarmonySlots.OwnsSite(site);
        return ReadHostOwnsSite(site);
    }

    /// <summary>Bootstrap config <c>Diagnostics.SkipCoreHarmony</c>.</summary>
    public static bool SkipCoreHarmony =>
        HarmonySlots?.SkipCoreHarmony == true || ReadHostBool("SkipCoreHarmony");

    /// <summary>Bootstrap config <c>Diagnostics.SkipCoreHarmonyAllowLiveWorld</c>.</summary>
    public static bool SkipCoreHarmonyAllowLiveWorld =>
        ReadHostBool("SkipCoreHarmonyAllowLiveWorld");

    /// <summary>Bootstrap config <c>Diagnostics.SkipCoreModules</c> (comma-separated names).</summary>
    public static string SkipCoreModules => ReadSkipModules();

    /// <summary>Bootstrap config <c>Diagnostics.SkipCoreModulesAllowLiveWorld</c>.</summary>
    public static bool SkipCoreModulesAllowLiveWorld => ReadSkipModulesAllowLiveWorld();

    /// <summary>
    /// Read Bootstrap's <c>CoreLayout.ProtocolVersion</c>. Missing assembly is -1
    /// (fallback path; Core owns lifetime). Missing field is 0 (refused). Mismatch
    /// throws before any slot is filled so an old Bootstrap never half-installs a
    /// new Core. Bootstrap treats this throw like a failed Install that filled no
    /// slots: the collectible ALC is released, not latched.
    /// </summary>
    internal static void EnsureBootstrapProtocol()
    {
        int host = ReadBootstrapProtocolOrAbsent();
        if (host < 0) return;
        if (host == ProtocolVersion) return;
        string error =
            "Core reload refused: Bootstrap protocol " + host.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " does not accept Core protocol " + ProtocolVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".";
        Plugin.Logger?.LogError(error);
        throw new InvalidOperationException(error);
    }

    /// <summary>
    /// Bootstrap's published protocol, or -1 when the assembly is not loaded.
    /// Missing type/field is 0.
    /// </summary>
    internal static int ReadBootstrapProtocolOrAbsent()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, AssemblySimpleName, StringComparison.Ordinal))
                continue;
            try
            {
                var type = asm.GetType("Progenitor.Bootstrap.CoreLayout");
                if (type == null) return 0;
                var field = type.GetField(
                    "ProtocolVersion",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (field == null || field.FieldType != typeof(int)) return 0;
                return field.GetValue(null) is int v ? v : 0;
            }
            catch
            {
                return 0;
            }
        }
        return -1;
    }

    /// <summary>
    /// Write one Harmony handler into Bootstrap's slot (Core content sites, or a plugin
    /// / extra-instance lazy site via <c>HarmonySlotHandoff.TryBindPlugin</c>). Returns
    /// false when Bootstrap has no trampoline for that original.
    /// </summary>
    public static bool TrySetHarmonySlot(string site, Delegate dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        if (string.IsNullOrEmpty(site)) throw new ArgumentException("site is required.", nameof(site));
        if (HarmonySlots != null) return HarmonySlots.TrySet(site, dispatcher);
        return TryInvokeHostHarmonySet(site, dispatcher);
    }

    /// <summary>
    /// Plugin bind with owner. Separate method name so
    /// <see cref="TrySetHarmonySlot"/> stays a unique two-arg reflection target.
    /// </summary>
    public static bool TryBindPluginSlot(string site, Delegate dispatcher, string owner)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        if (string.IsNullOrEmpty(site)) throw new ArgumentException("site is required.", nameof(site));
        try
        {
            var host = _hostType ?? FindHostType();
            if (host != null)
            {
                var mi = host.GetMethod("TryBindPluginSlot", BindingFlags.Public | BindingFlags.Static);
                if (mi != null)
                    return mi.Invoke(null, new object?[] { site, dispatcher, owner ?? "" }) is true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"TryBindPluginSlot: {e.Message}");
            return false;
        }
        return TrySetHarmonySlot(site, dispatcher);
    }

    /// <summary>Null one plugin slot when <paramref name="owner"/> matches (empty owner force-clears).</summary>
    public static bool TryClearPluginSlot(string site, string? owner)
    {
        if (string.IsNullOrEmpty(site)) return false;
        try
        {
            var host = _hostType ?? FindHostType();
            if (host != null)
            {
                var mi = host.GetMethod("TryClearPluginSlot", BindingFlags.Public | BindingFlags.Static);
                if (mi != null)
                    return mi.Invoke(null, new object?[] { site, owner }) is true;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"TryClearPluginSlot: {e.Message}");
        }
        ClearHarmonySlot(site);
        return true;
    }

    /// <summary>Null every plugin slot bound by <paramref name="owner"/>.</summary>
    public static void ClearOwnedPluginSlots(string owner)
    {
        if (string.IsNullOrEmpty(owner)) return;
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("ClearOwnedPluginSlots", BindingFlags.Public | BindingFlags.Static);
            mi?.Invoke(null, new object?[] { owner });
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ClearOwnedPluginSlots: {e.Message}");
        }
    }

    /// <summary>ReloaderHost / fallback: drop plugin slots that pin this assembly / ALC.</summary>
    public static void ClearPluginSlotsForUnload(Assembly? assembly, string? alcName)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("ClearPluginSlotsForUnload", BindingFlags.Public | BindingFlags.Static);
            if (mi != null)
            {
                mi.Invoke(null, new object?[] { assembly, alcName });
                return;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ClearPluginSlotsForUnload: {e.Message}");
        }
        if (!string.IsNullOrEmpty(alcName))
            ClearOwnedPluginSlots(alcName);
        try
        {
            string? name = assembly?.GetName().Name;
            if (!string.IsNullOrEmpty(name))
                ClearOwnedPluginSlots(name);
        }
        catch { /* unloading */ }
    }

    /// <summary>Null one Harmony slot. No-op when Bootstrap is absent.</summary>
    public static void ClearHarmonySlot(string site)
    {
        if (string.IsNullOrEmpty(site)) return;
        if (HarmonySlots != null) { HarmonySlots.Clear(site); return; }
        TryInvokeHostHarmonySet(site, null);
    }

    /// <summary>Null every Harmony slot. No-op when Bootstrap is absent.</summary>
    public static void ClearAllHarmonySlots()
    {
        if (HarmonySlots != null) { HarmonySlots.ClearAll(); return; }
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return;
            var mi = host.GetMethod("ClearAllHarmonySlots", BindingFlags.Public | BindingFlags.Static);
            mi?.Invoke(null, Array.Empty<object>());
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"ClearAllHarmonySlots: {e.Message}");
        }
    }

    /// <summary>Matches Bootstrap <c>UiSlots.Dispatch</c>.</summary>
    public delegate void UiDispatch(ushort widgetId, byte kind, float number, bool flag, string? text);

    /// <summary>Matches Bootstrap <c>UiInputContext.KeyDispatch</c>.</summary>
    public delegate void UiKeyDispatch(int keyCode, bool down);

    /// <summary>Fill Bootstrap's process-lifetime UI click/value trampoline. False when Bootstrap is absent.</summary>
    public static bool TrySetUiDispatch(Delegate dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        return InvokeUiSlotsBool("TrySetDispatch", dispatcher);
    }

    public static void ClearUiDispatch() => InvokeUiSlots("ClearDispatch");

    public static bool TryWireUi(ushort widgetId, byte kind, object? unityEvent) =>
        InvokeUiSlotsBool("TryWire", widgetId, kind, unityEvent);

    public static void UnwireUi(ushort widgetId) => InvokeUiSlots("Unwire", widgetId);

    public static void UnwireUiMany(ushort[] ids) => InvokeUiSlots("UnwireMany", ids);

    public static bool TrySetUiKeys(Delegate dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        return InvokeUiInputBool("TrySetKeyDispatch", dispatcher);
    }

    public static void ClearUiKeys() => InvokeUiInput("ClearKeyDispatch");

    public static void SetUiCapture(bool capture) => InvokeUiInput("SetCapture", capture);

    public static void ClearUiCapture() => InvokeUiInput("ClearCapture");

    public static void ClearAllUiSlots() => InvokeUiSlots("ClearAll");

    /// <summary>Matches Bootstrap <c>UiActionSlot.Dispatch</c> (job NATIVE1).</summary>
    internal delegate void UiActionDispatch(ushort slotId);

    private const string UiActionSlotTypeName = "Progenitor.Bootstrap.UiActionSlot";
    private static int _uiActionCapability = -1;

    /// <summary>
    /// Bootstrap's Action-field trampoline capability (<c>UiActionSlot.Capability</c>).
    /// <c>0</c> when Bootstrap is absent or predates the type — the native MODS outer tab then
    /// stays on the imitation route and reports <c>adapter-unavailable</c>. Not a protocol bump:
    /// either pairing is a valid behavior. Read once per Core generation.
    /// </summary>
    public static int UiActionCapability
    {
        get
        {
            if (_uiActionCapability >= 0) return _uiActionCapability;
            int value = 0;
            try
            {
                var type = FindBootstrapType(UiActionSlotTypeName);
                var field = type?.GetField("Capability", BindingFlags.Public | BindingFlags.Static);
                if (field?.GetRawConstantValue() is int constant) value = constant;
                else if (field?.GetValue(null) is int read) value = read;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("UiActionSlot.Capability: " + e.Message);
                value = 0;
            }
            _uiActionCapability = value;
            return value;
        }
    }

    /// <summary>Fill Bootstrap's process-lifetime Action-field trampoline dispatcher. False when absent.</summary>
    public static bool TrySetUiActionDispatch(Delegate dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        return InvokeUiActionBool("TrySetDispatch", dispatcher);
    }

    public static void ClearUiActionDispatch() => InvokeUiAction("ClearDispatch");

    /// <summary>
    /// Lease the Bootstrap-owned <c>Il2CppSystem.Action</c> for <paramref name="slotId"/>. The
    /// caller assigns it to the native field through typed interop and releases the id when the
    /// assignment is removed. False (null action) when Bootstrap does not offer the adapter.
    /// </summary>
    public static bool TryLeaseUiAction(ushort slotId, out object? action)
    {
        action = null;
        try
        {
            var type = FindBootstrapType(UiActionSlotTypeName);
            var mi = type?.GetMethod("TryLease", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            var args = new object?[] { slotId, null };
            bool ok = mi.Invoke(null, args) is true;
            action = ok ? args[1] : null;
            return ok && action != null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UiActionSlot.TryLease: " + e.Message);
            return false;
        }
    }

    public static void ReleaseUiAction(ushort slotId) => InvokeUiAction("Release", slotId);

    public static void ClearAllUiActions() => InvokeUiAction("ClearAll");

    private static bool InvokeUiActionBool(string method, params object?[] args)
    {
        try
        {
            var type = FindBootstrapType(UiActionSlotTypeName);
            var mi = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            return mi.Invoke(null, args) is true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UiActionSlot." + method + ": " + e.Message);
            return false;
        }
    }

    private static void InvokeUiAction(string method, params object?[] args)
    {
        try
        {
            var type = FindBootstrapType(UiActionSlotTypeName);
            var mi = type?.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            mi?.Invoke(null, args.Length == 0 ? Array.Empty<object>() : args);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("UiActionSlot." + method + ": " + e.Message);
        }
    }

    private const string TextInputQueueTypeName = "Progenitor.Bootstrap.TextInputQueue";
    private static int _textInputCapability = -1;
    private static Func<char[], int, int>? _textInputDrain;
    private static bool _textInputDrainBound;

    /// <summary>
    /// TEXTINPUT1. Bootstrap's <c>TextInputQueue.Capability</c>: <c>1</c> when this Bootstrap queues
    /// the OS's typed characters from <c>Keyboard.onTextInput</c>, <c>0</c> when Bootstrap is absent
    /// or predates it (the text fields then keep the physical-key map). Not a protocol bump. Read
    /// once per Core generation.
    /// </summary>
    public static int TextInputCapability
    {
        get
        {
            if (_textInputCapability >= 0) return _textInputCapability;
            int value = 0;
            try
            {
                var type = FindBootstrapType(TextInputQueueTypeName);
                var field = type?.GetField("Capability", BindingFlags.Public | BindingFlags.Static);
                if (field?.GetRawConstantValue() is int constant) value = constant;
                else if (field?.GetValue(null) is int read) value = read;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("TextInputQueue.Capability: " + e.Message);
                value = 0;
            }
            _textInputCapability = value;
            return value;
        }
    }

    /// <summary>
    /// TEXTINPUT1. Bootstrap's <c>TextInputQueue.Drain(char[] buffer, int minFrame)</c> as a delegate,
    /// bound once per Core generation; null when the capability is missing. The delegate targets a
    /// Bootstrap (non-collectible) static method, so holding it pins nothing.
    /// </summary>
    public static Func<char[], int, int>? TextInputDrain
    {
        get
        {
            if (_textInputDrainBound) return _textInputDrain;
            _textInputDrainBound = true;
            if (TextInputCapability < 1) return null;
            try
            {
                var type = FindBootstrapType(TextInputQueueTypeName);
                var mi = type?.GetMethod("Drain", BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(char[]), typeof(int) }, null);
                if (mi != null)
                    _textInputDrain = (Func<char[], int, int>)Delegate.CreateDelegate(typeof(Func<char[], int, int>), mi);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("TextInputQueue.Drain: " + e.Message);
                _textInputDrain = null;
            }
            return _textInputDrain;
        }
    }

    /// <summary>TEXTINPUT1 readback: Bootstrap's <c>TextInputQueue.State</c> and <c>Received</c>, or <c>absent</c>.</summary>
    public static (string state, long received) TextInputStatus
    {
        get
        {
            try
            {
                var type = FindBootstrapType(TextInputQueueTypeName);
                if (type == null) return ("absent", 0);
                string state = type.GetProperty("State", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string ?? "unknown";
                long received = type.GetProperty("Received", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is long n ? n : 0;
                return (state, received);
            }
            catch { return ("unreadable", 0); }
        }
    }

    private static int _connectRecordCapability = -1;

    /// <summary>
    /// Job SERVERINST5. Bootstrap's <c>ProcessHost.ConnectRecordCapability</c>: <c>1</c> when this
    /// Bootstrap records the joined server's address from <c>GameHelper.LaunchGame</c>, <c>0</c>
    /// when Bootstrap is absent or predates it. Not a protocol bump — an older Bootstrap is a valid
    /// pairing whose only consequence is that the graceful-stop gate reports
    /// <c>ServerProcessControl.ReasonBootstrapTooOld</c> instead of a port mismatch. Read once per
    /// Core generation.
    /// </summary>
    public static int ConnectRecordCapability
    {
        get
        {
            if (_connectRecordCapability >= 0) return _connectRecordCapability;
            int value = 0;
            try
            {
                var host = _hostType ?? FindHostType();
                var field = host?.GetField("ConnectRecordCapability", BindingFlags.Public | BindingFlags.Static);
                if (field?.GetRawConstantValue() is int constant) value = constant;
                else if (field?.GetValue(null) is int read) value = read;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ProcessHost.ConnectRecordCapability: " + e.Message);
                value = 0;
            }
            _connectRecordCapability = value;
            return value;
        }
    }

    /// <summary>True when this Bootstrap records the joined server's port at all.</summary>
    public static bool RecordsConnectPort => ConnectRecordCapability >= 1;

    private static int _admissionSnapshotCapability = -1;

    /// <summary>
    /// Job HOT2. Bootstrap's <c>ProcessHost.AdmissionSnapshotCapability</c>: <c>1</c> when this
    /// Bootstrap can capture the loader-state snapshot the install lane's admission evaluator needs,
    /// <c>0</c> when Bootstrap is absent or predates it. Not a protocol bump — an older Bootstrap is
    /// a valid pairing whose only consequence is that every admission is
    /// <c>AdmissionReasonCode.SnapshotUnavailable</c> and installs take the restart route. Read once
    /// per Core generation.
    /// </summary>
    public static int AdmissionSnapshotCapability
    {
        get
        {
            if (_admissionSnapshotCapability >= 0) return _admissionSnapshotCapability;
            int value = 0;
            try
            {
                var host = _hostType ?? FindHostType();
                var field = host?.GetField("AdmissionSnapshotCapability", BindingFlags.Public | BindingFlags.Static);
                if (field?.GetRawConstantValue() is int constant) value = constant;
                else if (field?.GetValue(null) is int read) value = read;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("ProcessHost.AdmissionSnapshotCapability: " + e.Message);
                value = 0;
            }
            _admissionSnapshotCapability = value;
            return value;
        }
    }

    /// <summary>
    /// Capture Bootstrap's admission snapshot for one plugin GUID and the candidate file the caller
    /// intends to write. False means "no snapshot": Bootstrap is absent, predates
    /// <see cref="AdmissionSnapshotCapability"/>, or the call threw — all of which the caller turns
    /// into an unavailable snapshot, never into a quiet one.
    /// </summary>
    internal static bool TryCaptureAdmissionSnapshot(
        string pluginId,
        string? fileName,
        string? sha256,
        out Install.Admission.BootstrapSnapshotValues? values)
    {
        values = null;
        if (AdmissionSnapshotCapability < 1) return false;
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("CaptureAdmissionSnapshot", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            object? snapshot = mi.Invoke(null, new object?[] { pluginId ?? "", fileName, sha256 });
            if (snapshot == null) return false;
            var type = snapshot.GetType();
            values = new Install.Admission.BootstrapSnapshotValues(
                ReadBool(type, snapshot, "ServerProcess"),
                ReadLong(type, snapshot, "Generation"),
                ReadBool(type, snapshot, "LoaderBusy"),
                ReadBool(type, snapshot, "PriorGenerationPinned"),
                ReadBool(type, snapshot, "PluginLoaded"),
                ReadText(type, snapshot, "PluginName"),
                ReadText(type, snapshot, "PluginVersion"),
                ReadText(type, snapshot, "PluginImageSha256"),
                ReadLong(type, snapshot, "PluginImageSize"),
                ReadBool(type, snapshot, "UnsafeHarmonyMarked"),
                ReadBool(type, snapshot, "AllowlistAllows"),
                ReadText(type, snapshot, "AllowlistStatus"),
                ReadText(type, snapshot, "ReloadFolder"),
                ReadBool(type, snapshot, "ProcessLifetimeMarked"));
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ProcessHost.CaptureAdmissionSnapshot: " + e.Message);
            return false;
        }
    }

    private static bool ReadBool(Type type, object instance, string name)
    {
        try { return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) is true; }
        catch { return false; }
    }

    private static long ReadLong(Type type, object instance, string name)
    {
        try
        {
            object? value = type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance);
            return value switch { long l => l, int i => i, _ => 0L };
        }
        catch { return 0L; }
    }

    private static string ReadText(Type type, object instance, string name)
    {
        try { return type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance)?.GetValue(instance) as string ?? ""; }
        catch { return ""; }
    }

    /// <summary>
    /// FORD1: <c>ProcessHost.WaitingPlugins()</c> — reload-folder DLLs waiting for a dependency. Empty with an
    /// older Bootstrap (no such method).
    /// </summary>
    public static string[] WaitingPluginRows()
    {
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("WaitingPlugins", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            return mi?.Invoke(null, null) as string[] ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// FINJ1: <c>ProcessHost.ProcessLifetimePlugins()</c> — reload-folder plugins Bootstrap keeps until restart
    /// because they registered their own IL2CPP types. Empty with an older Bootstrap (no such method).
    /// </summary>
    public static string[] ProcessLifetimePluginRows()
    {
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("ProcessLifetimePlugins", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
            return mi?.Invoke(null, null) as string[] ?? Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static MethodInfo? _consoleLogCost;
    private static bool _consoleLogCostLooked;

    /// <summary>
    /// <c>ProcessHost.ConsoleLogCost(bool)</c>: the queued console's counters (see <c>ConsoleLogQueue.ToArray</c> in
    /// Bootstrap), or null when the console is not queued or Bootstrap is older. Read once per hitch-summary window.
    /// </summary>
    public static long[]? ConsoleLogCost(bool resetMaxima)
    {
        try
        {
            if (!_consoleLogCostLooked)
            {
                var host = _hostType ?? FindHostType();
                if (host == null) return null;
                _consoleLogCost = host.GetMethod("ConsoleLogCost", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(bool) }, null);
                _consoleLogCostLooked = true;
            }
            return _consoleLogCost?.Invoke(null, new object[] { resetMaxima }) as long[];
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Game port of the client's last recorded join, or 0 (absent/older Bootstrap, or no join yet).</summary>
    public static int LastConnectPort => ReadHostInt("LastConnectPort");

    /// <summary>Host of the client's last recorded join, or "".</summary>
    public static string LastConnectAddress => ReadHostString("LastConnectAddress");

    /// <summary>Transport of the client's last recorded join, or "".</summary>
    public static string LastConnectProtocol => ReadHostString("LastConnectProtocol");

    /// <summary>
    /// Bootstrap's process-lifetime <c>progenitor.cfg</c> (<c>ProcessHost.SharedConfig</c>),
    /// or null when Bootstrap is absent or predates the handover. Core installs inside Bootstrap's
    /// <c>Load</c>, while the chainloader's entry for Bootstrap still has no instance, so this is
    /// the only way to reach the file at process start. <see cref="BepInEx.Configuration.ConfigFile"/>
    /// is a BepInEx type: holding it pins nothing collectible.
    /// </summary>
    public static BepInEx.Configuration.ConfigFile? SharedConfig
    {
        get
        {
            try
            {
                var host = _hostType ?? FindHostType();
                return host?.GetProperty("SharedConfig", BindingFlags.Public | BindingFlags.Static)?.GetValue(null)
                    as BepInEx.Configuration.ConfigFile;
            }
            catch
            {
                return null;
            }
        }
    }

    private static int ReadHostInt(string property)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return 0;
            var value = host.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return value is int i ? i : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string ReadHostString(string property)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return "";
            return host.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// JOINFLOW2: Bootstrap's <c>IsHostingPrivateGame</c> (a hosted private game child of this client is running),
    /// or false when Bootstrap is absent or predates it. Read-only; no Bootstrap change.
    /// </summary>
    public static bool IsHostingPrivateGame
    {
        get
        {
            try
            {
                var host = _hostType ?? FindHostType();
                return host?.GetProperty("IsHostingPrivateGame", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Matches Bootstrap <c>UiMaskDetour.MaskApply</c>. Same shape as the detour's own signature so
    /// one Core method serves both owners; the game's body has already run when this is called, and
    /// Core's copy of the trampoline is null on that path so it does not run it twice.
    /// </summary>
    public delegate void UiMaskApply(IntPtr instance, IntPtr blockInputState, IntPtr methodInfo);

    /// <summary>
    /// Job FLATCH3 (R45 finding 19). Hand the <c>GetConsumedInputs</c> input-mask detour to
    /// Bootstrap, which is not collectible and can hold it for the life of the process, and point
    /// its slot at this generation's mask body. <paramref name="address"/> is resolved on this
    /// side because the address library is Core's.
    ///
    /// <para><b>False is not an error.</b> It means Bootstrap is absent, predates this method, or
    /// could not apply the detour, and the caller then owns the <c>INativeDetour</c> itself the
    /// way it always did — which is why adding this is not a protocol bump. See
    /// <c>GameInputBlocker.EnsureMask</c> for the two paths and
    /// <c>Progenitor.Bootstrap.UiMaskDetour</c> for what the hand-off buys: teardown stops being a
    /// native unpatch that has to happen on the game main thread.</para>
    /// </summary>
    public static bool TryInstallUiMask(IntPtr address, Delegate apply)
    {
        if (apply == null) throw new ArgumentNullException(nameof(apply));
        return InvokeUiMaskBool("TryInstall", address, apply);
    }

    /// <summary>
    /// Null Bootstrap's mask slot. One volatile store on the far side: it frees no native memory
    /// and is safe from any thread, which is the whole point of the hand-off. No-op when Bootstrap
    /// is absent or never took the detour.
    /// </summary>
    public static void ClearUiMask() => InvokeUiMask("ClearMask");

    /// <summary>
    /// Process-lifetime Sprite cache owned by Bootstrap. The Sprite and its Texture2D
    /// are one entry with <c>hideFlags = HideAndDontSave</c>; Core must not
    /// <c>Destroy</c> them on Uninstall. False when Bootstrap is absent (fallback decode).
    /// </summary>
    public static bool TryGetOrCreateSprite(string key, byte[] bytes, out UnityEngine.Sprite? sprite)
    {
        sprite = null;
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("TryGetOrCreateSprite", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            object?[] args = { key, bytes, null };
            if (mi.Invoke(null, args) is not true) return false;
            sprite = args[2] as UnityEngine.Sprite;
            return sprite != null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("TryGetOrCreateSprite: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Lookup by plugin id + relative path when PNG bytes are not on disk (Core swap
    /// re-apply before the plugin asset root can be probed).
    /// </summary>
    public static bool TryGetCachedSprite(string pluginId, string relativePath, out UnityEngine.Sprite? sprite)
    {
        sprite = null;
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("TryGetCachedSprite", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            object?[] args = { pluginId, relativePath, null };
            if (mi.Invoke(null, args) is not true) return false;
            sprite = args[2] as UnityEngine.Sprite;
            return sprite != null;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("TryGetCachedSprite: " + e.Message);
            return false;
        }
    }

    /// <summary>Destroy one cached pair. No-op when Bootstrap is absent.</summary>
    public static bool ReleaseSprite(string key)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("ReleaseSprite", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            return mi.Invoke(null, new object?[] { key }) is true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ReleaseSprite: " + e.Message);
            return false;
        }
    }

    /// <summary>Destroy every cached pair for this plugin. No-op when Bootstrap is absent.</summary>
    public static int ReleasePluginSprites(string pluginId)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("ReleasePluginSprites", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return 0;
            return mi.Invoke(null, new object?[] { pluginId }) is int n ? n : 0;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("ReleasePluginSprites: " + e.Message);
            return 0;
        }
    }

    /// <summary>
    /// True when Uninstall may <c>DestroyImmediate</c> UI roots. False (or Bootstrap
    /// absent with no collectible swap) uses deferred <c>Object.Destroy</c>. Live
    /// Bootstrap returns <c>CoreReloadPolicy.MayDestroyImmediateOnUninstall</c>
    /// (frame gap honored). Default true: no collectible swap is in flight.
    /// </summary>
    public static bool MayDestroyUiImmediate()
    {
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("MayDestroyUiImmediate", BindingFlags.Public | BindingFlags.Static);
            if (mi?.Invoke(null, null) is bool b) return b;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("MayDestroyUiImmediate: " + e.Message);
        }
        return true;
    }

    private static bool TryInvokeHostHarmonySet(string site, Delegate? dispatcher)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return false;
            var mi = host.GetMethod("TrySetHarmonySlot", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            return mi.Invoke(null, new object?[] { site, dispatcher }) is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Write Core's client dispatcher into Bootstrap's slot. Returns false when Bootstrap is
    /// absent or did not apply the client detour, so Core may own the JMP as a fallback.
    /// </summary>
    public static bool TrySetClientReceiveSlot(Delegate dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        if (ReceiveSlots != null) return ReceiveSlots.TrySetClient(dispatcher);
        return TryInvokeHostSet("TrySetClientReceiveSlot", dispatcher);
    }

    /// <summary>Write Core's server dispatcher into Bootstrap's slot. See <see cref="TrySetClientReceiveSlot"/>.</summary>
    public static bool TrySetServerReceiveSlot(Delegate dispatcher)
    {
        if (dispatcher == null) throw new ArgumentNullException(nameof(dispatcher));
        if (ReceiveSlots != null) return ReceiveSlots.TrySetServer(dispatcher);
        return TryInvokeHostSet("TrySetServerReceiveSlot", dispatcher);
    }

    /// <summary>Null the client slot. No-op when Bootstrap is absent.</summary>
    public static void ClearClientReceiveSlot()
    {
        if (ReceiveSlots != null) { ReceiveSlots.ClearClient(); return; }
        TryInvokeHostSet("TrySetClientReceiveSlot", null);
    }

    /// <summary>Null the server slot. No-op when Bootstrap is absent.</summary>
    public static void ClearServerReceiveSlot()
    {
        if (ReceiveSlots != null) { ReceiveSlots.ClearServer(); return; }
        TryInvokeHostSet("TrySetServerReceiveSlot", null);
    }

    /// <summary>
    /// Bind Core's four material seam handlers into Bootstrap's opt-in seams (P3 spike), or clear them
    /// with four nulls. False when Bootstrap is absent, older than the seams, or did not arm them.
    /// </summary>
    internal static bool TrySetMaterialSlots(Delegate? countGate, Delegate? compose, Delegate? payGate, Delegate? pay)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("TrySetMaterialSlots", BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            return mi.Invoke(null, new object?[] { countGate, compose, payGate, pay }) is true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("materials: binding Bootstrap seams threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Bootstrap's <c>materials:</c> state line, or a line saying this Bootstrap has no material seams.</summary>
    internal static string MaterialSeamsDescribe()
    {
        try
        {
            var host = _hostType ?? FindHostType();
            var mi = host?.GetMethod("MaterialSeamsDescribe", BindingFlags.Public | BindingFlags.Static);
            if (mi?.Invoke(null, null) is string s) return s;
        }
        catch { /* fall through */ }
        return "materials: this Bootstrap has no material seams (update Bootstrap and restart to arm them)";
    }

    private static bool TryInvokeHostSet(string method, Delegate? dispatcher)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return false;
            var mi = host.GetMethod(method, BindingFlags.Public | BindingFlags.Static);
            if (mi == null) return false;
            // Explicit array: a null dispatcher must not become a null params array.
            return mi.Invoke(null, new object?[] { dispatcher }) is true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Record an event id Core owns on Bootstrap's process-lifetime table. Safe when the
    /// adapter is missing: falls back to invoking ProcessHost by name so plugin fourccs
    /// still drop while the slot is empty.
    /// </summary>
    public static void RegisterOwnedEventId(int eventId)
    {
        try
        {
            if (ReceiveSlots != null)
            {
                ReceiveSlots.RegisterOwnedEventId(eventId);
                return;
            }
            var host = _hostType ?? FindHostType();
            if (host == null) return;
            Invoke(host, "RegisterOwnedEventId", eventId);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"RegisterOwnedEventId(0x{eventId:X8}): {e.Message}");
        }
    }

    /// <summary>
    /// Retry wiring Bootstrap's log ring as <see cref="Dev.LogBuffer"/>'s backend. Called
    /// from Read/LatestSeq/Install so a failed LogAdapter at attach cannot leave Core
    /// serving its unused empty ring while Bootstrap is the owner.
    /// </summary>
    internal static void EnsureLogBackend()
    {
        if (Logs != null)
        {
            Dev.LogBuffer.Backend = Logs;
            return;
        }
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return;
            _hostType ??= host;
            TryWireLogs(host, onlyIfRingPresent: true);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"EnsureLogBackend: {e.Message}");
        }
    }

    private static bool ReadHostBool(string property)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return false;
            return host.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;
        }
        catch
        {
            return false;
        }
    }

    private static Type? FindPolicyType()
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            if (!string.Equals(asm.GetName().Name, AssemblySimpleName, StringComparison.Ordinal))
                continue;
            return asm.GetType("Progenitor.Bootstrap.HarmonySlotPolicy");
        }
        return null;
    }

    private static string ReadSkipModules()
    {
        try
        {
            var policy = FindPolicyType();
            if (policy == null) return "";
            var mi = policy.GetMethod("CurrentSkipModules", BindingFlags.Public | BindingFlags.Static);
            if (mi != null)
                return mi.Invoke(null, Array.Empty<object>()) as string ?? "";
            return policy.GetProperty("SkipModulesValue", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) as string ?? "";
        }
        catch
        {
            return "";
        }
    }

    private static bool ReadSkipModulesAllowLiveWorld()
    {
        try
        {
            var policy = FindPolicyType();
            if (policy == null) return false;
            var mi = policy.GetMethod("CurrentSkipModulesAllowLiveWorld", BindingFlags.Public | BindingFlags.Static);
            if (mi != null)
                return mi.Invoke(null, Array.Empty<object>()) is true;
            return policy.GetProperty("SkipModulesAllowLiveWorld", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class MainThreadAdapter : IMainThreadBackend
    {
        private readonly Type _host;
        private readonly MethodInfo? _runDef;

        public MainThreadAdapter(Type host)
        {
            _host = host;
            // Run is optional at construction: a version-skewed Bootstrap that still has
            // Post/Drain must not fail the whole adapter (that stranded Core's queue).
            _runDef = host.GetMethod("Run", BindingFlags.Public | BindingFlags.Static);
        }

        public bool IsMainThread => (bool)_host.GetProperty("IsMainThread", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        public void Post(Action action, object? owner) => Invoke(_host, "Post", action, owner);

        public Task<T> Run<T>(Func<T> func, object? owner)
        {
            if (_runDef == null)
                throw new MissingMethodException(_host.FullName, "Run");
            try
            {
                return (Task<T>)_runDef.MakeGenericMethod(typeof(T)).Invoke(null, new object?[] { func, owner })!;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }

        public void PostDelayed(double seconds, Action action, object? owner) =>
            Invoke(_host, "PostDelayed", seconds, action, owner);

        public void DropOwnedBy(object? plugin, Assembly? assembly, string? contextName) =>
            Invoke(_host, "DropOwnedBy", plugin, assembly, contextName);

        public void Drain() => Invoke(_host, "Drain");
    }

    private sealed class LogAdapter : ILogBackend
    {
        private readonly Type _host;

        public LogAdapter(Type host) => _host = host;

        public long LatestSeq => (long)_host.GetProperty("LatestSeq", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        public List<Dev.LogBuffer.Entry> Read(long since, int max, string? filter, LogLevel? minLevel)
        {
            int? minValue = minLevel.HasValue ? (int)minLevel.Value : null;
            var raw = Invoke(_host, "ReadLogs", since, max, filter, minValue);
            if (raw is not System.Collections.IEnumerable rows)
                return new List<Dev.LogBuffer.Entry>();
            var result = new List<Dev.LogBuffer.Entry>();
            foreach (var row in rows)
            {
                if (row == null) continue;
                var t = row.GetType();
                long seq = Convert.ToInt64(t.GetProperty("Seq")?.GetValue(row) ?? 0L);
                var time = t.GetProperty("Time")?.GetValue(row) is DateTime dt ? dt : DateTime.UtcNow;
                var level = (LogLevel)(int)(t.GetProperty("Level")?.GetValue(row) ?? 0);
                string source = t.GetProperty("Source")?.GetValue(row) as string ?? "?";
                string text = t.GetProperty("Text")?.GetValue(row) as string ?? "";
                result.Add(new Dev.LogBuffer.Entry(seq, time, level, source, text));
            }
            return result;
        }
    }

    private sealed class ReloaderAdapter : IReloaderBackend
    {
        private readonly Type _host;
        private readonly MethodInfo _tryGet;

        public ReloaderAdapter(Type host)
        {
            _host = host;
            _tryGet = host.GetMethod("TryGetSourcePath", BindingFlags.Public | BindingFlags.Static)
                ?? throw new MissingMethodException(host.FullName, "TryGetSourcePath");
        }

        public string Folder => (string)_host.GetProperty("ReloadFolder", BindingFlags.Public | BindingFlags.Static)!.GetValue(null)!;

        public List<string> RescanNow() => CopyStrings(Invoke(_host, "RescanNow"));

        public List<string> LoadedPluginNames() => CopyStrings(Invoke(_host, "LoadedPluginNames"));

        public bool TryGetSourcePath(Assembly asm, out string path)
        {
            object?[] args = { asm, "" };
            try
            {
                bool ok = (bool)_tryGet.Invoke(null, args)!;
                path = args[1] as string ?? "";
                return ok;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }

        private static List<string> CopyStrings(object raw)
        {
            var list = new List<string>();
            if (raw is System.Collections.IEnumerable rows)
            {
                foreach (var row in rows)
                    if (row is string s) list.Add(s);
            }
            return list;
        }
    }

    private sealed class ReceiveSlotAdapter : IReceiveSlotBackend
    {
        private readonly Type _host;
        private readonly MethodInfo? _setClient;
        private readonly MethodInfo? _setServer;
        private readonly MethodInfo? _registerOwned;

        public ReceiveSlotAdapter(Type host)
        {
            _host = host;
            _setClient = host.GetMethod("TrySetClientReceiveSlot", BindingFlags.Public | BindingFlags.Static);
            _setServer = host.GetMethod("TrySetServerReceiveSlot", BindingFlags.Public | BindingFlags.Static);
            _registerOwned = host.GetMethod("RegisterOwnedEventId", BindingFlags.Public | BindingFlags.Static);
        }

        public bool OwnsClient => _host.GetProperty("OwnsClientReceive", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;

        public bool OwnsServer => _host.GetProperty("OwnsServerReceive", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;

        public bool TrySetClient(Delegate dispatcher) => InvokeSet(_setClient, dispatcher);

        public bool TrySetServer(Delegate dispatcher) => InvokeSet(_setServer, dispatcher);

        public void ClearClient() => InvokeSet(_setClient, null);

        public void ClearServer() => InvokeSet(_setServer, null);

        public void RegisterOwnedEventId(int eventId)
        {
            if (_registerOwned == null) return;
            try
            {
                _registerOwned.Invoke(null, new object[] { eventId });
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }

        private static bool InvokeSet(MethodInfo? method, Delegate? dispatcher)
        {
            if (method == null) return false;
            try
            {
                return method.Invoke(null, new object?[] { dispatcher }) is true;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }
    }

    private static bool ReadHostOwnsSite(string site)
    {
        try
        {
            var host = _hostType ?? FindHostType();
            if (host == null) return false;
            var mi = host.GetMethod("OwnsHarmonySite", BindingFlags.Public | BindingFlags.Static);
            if (mi != null)
                return mi.Invoke(null, new object?[] { site }) is true;
            return host.GetProperty("OwnsHarmonyPatches", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;
        }
        catch
        {
            return false;
        }
    }

    private sealed class HarmonySlotAdapter : IHarmonySlotBackend
    {
        private readonly Type _host;
        private readonly MethodInfo? _set;
        private readonly MethodInfo? _clearAll;
        private readonly MethodInfo? _ownsSite;

        public HarmonySlotAdapter(Type host)
        {
            _host = host;
            _set = host.GetMethod("TrySetHarmonySlot", BindingFlags.Public | BindingFlags.Static);
            _clearAll = host.GetMethod("ClearAllHarmonySlots", BindingFlags.Public | BindingFlags.Static);
            _ownsSite = host.GetMethod("OwnsHarmonySite", BindingFlags.Public | BindingFlags.Static);
        }

        public bool OwnsPatches =>
            _host.GetProperty("OwnsHarmonyPatches", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;

        public bool SkipCoreHarmony =>
            _host.GetProperty("SkipCoreHarmony", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true;

        public bool OwnsSite(string site)
        {
            if (_ownsSite != null)
            {
                try { return _ownsSite.Invoke(null, new object?[] { site }) is true; }
                catch (TargetInvocationException e) when (e.InnerException != null) { throw e.InnerException; }
            }
            return OwnsPatches;
        }

        public bool TrySet(string site, Delegate dispatcher)
        {
            if (_set == null) return false;
            try
            {
                return _set.Invoke(null, new object?[] { site, dispatcher }) is true;
            }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }

        public void Clear(string site)
        {
            if (_set == null) return;
            try { _set.Invoke(null, new object?[] { site, null }); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }

        public void ClearAll()
        {
            if (_clearAll == null) return;
            try { _clearAll.Invoke(null, Array.Empty<object>()); }
            catch (TargetInvocationException e) when (e.InnerException != null)
            {
                throw e.InnerException;
            }
        }
    }
}
