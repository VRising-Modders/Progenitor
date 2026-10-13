#if !PROGENITOR_TESTS
using System.Runtime.InteropServices;
using BepInEx.Unity.IL2CPP.Hook;
using Progenitor.Interop;
using Stunlock.Core;
#endif
using System;

namespace Progenitor.Native;

/// <summary>
/// Observe-only native detour on the managed-data dictionary's <c>FindEntry</c>. Spike 2 used
/// this to prove PrefabLookupKey lookups; Core owns the site so it resolves through the
/// address library instead of a hard-coded prefix. Not installed at boot: call
/// <see cref="Install"/> from a diagnostic and dispose the handle. The original is always
/// invoked; the hook never calls into the game.
///
/// The JMP target is a Core static, so a Core swap must <see cref="Uninstall"/> before the
/// generation is released (NativePinRegistry then drops the pin). A second <see cref="Install"/>
/// while a probe is live returns null so that handle cannot dispose someone else's JMP.
/// </summary>
public static
#if !PROGENITOR_TESTS
    unsafe
#endif
    class KeyTableProbe
{
#if !PROGENITOR_TESTS
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int FindEntryFn(IntPtr self, byte* key, IntPtr methodInfo);
#endif

    /// <summary>
    /// NativePinRegistry name for the FindEntry hook. Same generation as other Core pins;
    /// Uninstall disposes the JMP and does not <c>DropByName</c> (generation release frees it).
    /// </summary>
    public const string PinName = "keyprobe.findentry";

    private static readonly object Gate = new();
#if !PROGENITOR_TESTS
    private static INativeDetour? _detour;
    private static FindEntryFn? _original;
    private static FindEntryFn? _hook;
    private static int _target;
    private static int _source;
    private static long _calls;
    private static int _logged;
    private const int LogLimit = 80;
#endif
    private static bool _installed;
    private static int _token;

    /// <summary>True while a FindEntry JMP (or the test stand-in) is live.</summary>
    public static bool IsInstalled
    {
        get { lock (Gate) return _installed; }
    }

    /// <summary>
    /// Apply the observe-only JMP. Returns null when the site could not be resolved, or when
    /// a probe is already live (a second handle must not be able to Uninstall the first).
    /// Dispose uninstalls only if this handle still owns the live token.
    /// </summary>
    public static IDisposable? Install(int targetGuid, int sourceGuid)
    {
        lock (Gate)
        {
            if (_installed) return null;
#if PROGENITOR_TESTS
            _ = targetGuid;
            _ = sourceGuid;
            _token++;
            int token = _token;
            _installed = true;
            try
            {
                Progenitor.Bootstrap.NativePinRegistry.KeepDelegate(
                    (Action)(() => { }),
                    PinName,
                    "progenitor-core:test");
            }
            catch (Exception)
            {
                _installed = false;
                return null;
            }
            return new Handle(token);
#else
            _target = targetGuid;
            _source = sourceGuid;
            _calls = 0;
            _logged = 0;
            try
            {
                var dictType = typeof(Il2CppSystem.Collections.Generic.Dictionary<PrefabLookupKey, Il2CppSystem.Object>);
                var resolved = DetourSite.Resolve(
                    AddressId.SiteKeyProbeFindEntry,
                    AddressId.ManagedDataFindEntry,
                    () => Il2CppNative.MethodPointerByPrefix(dictType, "FindEntry"));
                if (!resolved.Ok)
                    return null;

                _hook = Hook;
                _detour = INativeDetour.CreateAndApply(resolved.Address, _hook, out _original);
                _token++;
                int token = _token;
                _installed = true;
                try { BootstrapLink.TryFunctionPointer(_hook, PinName, out _); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("KeyTableProbe: NativePinRegistry pin: " + e.Message);
                }
                Plugin.Logger?.LogInfo(
                    "KeyTableProbe: FindEntry installed at 0x"
                    + resolved.Address.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                    + " path=" + resolved.Path
                    + " (target " + targetGuid + ", source " + sourceGuid + ").");
                return new Handle(token);
            }
            catch (Exception e)
            {
                _detour = null;
                _original = null;
                _hook = null;
                _installed = false;
                Plugin.Logger?.LogError("KeyTableProbe: install failed: " + e);
                return null;
            }
#endif
        }
    }

    /// <summary>
    /// Drop the JMP. Safe to call twice. Invalidates live handles so a stale Dispose is a
    /// no-op. Process-lifetime receive detours are not touched. Core <c>Plugin.Uninstall</c>
    /// calls this before the generation's NativePinRegistry pins are released.
    /// </summary>
    public static void Uninstall()
    {
        lock (Gate) UninstallUnlocked();
    }

    private static void UninstallUnlocked()
    {
#if !PROGENITOR_TESTS
        try { _detour?.Dispose(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("KeyTableProbe: dispose: " + e.Message);
        }
        _detour = null;
        _original = null;
        _hook = null;
#endif
        _installed = false;
        _token++;
#if PROGENITOR_TESTS
        try { Progenitor.Bootstrap.NativePinRegistry.DropByName(PinName); }
        catch { /* tests must not leak pins; production leaves the pin for generation release */ }
#endif
    }

    public static string Summary()
    {
#if PROGENITOR_TESTS
        lock (Gate) return "FindEntry probe: installed=" + (_installed ? "true" : "false");
#else
        lock (Gate) return "FindEntry probe: calls=" + _calls + " logged=" + _logged;
#endif
    }

#if !PROGENITOR_TESTS
    private static int Hook(IntPtr self, byte* key, IntPtr methodInfo)
    {
        var original = _original;
        int result = original == null ? 0 : original(self, key, methodInfo);
        try
        {
            _calls++;
            if (key == null || _logged >= LogLimit) return result;
            int a = *(int*)key;
            int b = *(int*)(key + 8);
            if (a != _target && b != _target && a != _source && b != _source) return result;
            _logged++;
            long q0 = *(long*)key;
            long q1 = *(long*)(key + 8);
            string which = (a == _target || b == _target) ? "TARGET" : "source";
            Plugin.Logger?.LogInfo(
                "KeyTableProbe: FindEntry " + which
                + " dict=0x" + self.ToInt64().ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                + " q0=0x" + q0.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                + " q1=0x" + q1.ToString("X", System.Globalization.CultureInfo.InvariantCulture)
                + " -> " + result);
        }
        catch
        {
            // Never throw across the native boundary.
        }
        return result;
    }
#endif

    private sealed class Handle : IDisposable
    {
        private readonly int _handleToken;
        private bool _disposed;

        public Handle(int token) => _handleToken = token;

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Gate)
            {
                if (_handleToken != _token) return;
                UninstallUnlocked();
            }
        }
    }
}
