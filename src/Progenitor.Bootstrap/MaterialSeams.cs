using System;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP.Hook;
using ProjectM.Shared;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime native seams for <c>ctx.MaterialSource</c> (P3 spike). Opt-in: nothing is
/// detoured unless <c>[Materials] NativeSeams = true</c> was set before the process started.
///
/// <para><b>Availability</b> (both sides): the castle-heart overload of the merged count. With no Core
/// handler bound, or when Core says the call is not governed, the game's own array is returned
/// untouched. When governed, Bootstrap also asks the game for the same count without the castle heart
/// (bag, equipment and the eligible interaction inventory only), hands both arrays to Core, and points
/// the returned array at a Bootstrap-owned buffer holding the bag rows plus Core's pool rows.</para>
///
/// <para><b>Payment</b> (server): the castle-heart overload of the merged removal that returns the
/// item entity. When governed, the game first pays from the bag and the interaction inventory (the
/// same call with no castle heart, so the game's own shared storage is skipped), then Core pays what
/// is left from the provider's pool, all or nothing, and the remainder and item entity are written
/// back exactly as the game's own removal would.</para>
///
/// <para>Rules this file keeps: never throw across the native boundary; never call into a Core that is
/// gone (slots are nulled at Core uninstall and read once per call); re-entrant calls go straight to
/// the original; Core's delegates are pinned per Core generation like the receive slots.</para>
/// </summary>
internal static unsafe class MaterialSeams
{
    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeArrayHeader
    {
        public IntPtr Buffer;        // m_Buffer, offset 0x0
        public int Length;           // m_Length, offset 0x8
        public int AllocatorLabel;   // m_AllocatorLabel, offset 0xC
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate NativeArrayHeader* CountFn(NativeArrayHeader* ret, IntPtr entityManager, ulong castleHeart, ulong target,
        byte includeInteracting, byte includeShared, IntPtr methodInfo);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RemoveFn(IntPtr entityManager, ulong castleHeart, ulong target, int itemType, int amount,
        int* remainder, ulong* itemEntity, byte includeInteracting, byte includeShared, byte destroyItem, IntPtr methodInfo);

    // ---- Core slot shapes. Core passes delegates with identical signatures; they are re-bound here.
    /// <summary>Is this count governed? (target, heart)</summary>
    internal delegate bool CountGate(ulong target, ulong castleHeart);

    /// <summary>
    /// Compose the governed array: copy the bag rows and append pool rows into <c>outRows</c>. Returns the
    /// row count, a count larger than <c>capacity</c> to ask for more room, or -1 to keep the game's array.
    /// </summary>
    internal delegate int CountCompose(ulong target, ulong castleHeart, IntPtr fullRows, int fullLength,
        IntPtr bagRows, int bagLength, IntPtr outRows, int capacity);

    /// <summary>Is this payment line governed? (target, heart, item)</summary>
    internal delegate bool PayGate(ulong target, ulong castleHeart, int itemType);

    /// <summary>Pay <c>amount</c> from the pool; returns what is still owed and the item entity taken (0 for none).</summary>
    internal delegate int Pay(ulong target, ulong castleHeart, int itemType, int amount, bool destroyItem, out ulong itemEntity);

    private static CountGate? _countGate;
    private static CountCompose? _compose;
    private static PayGate? _payGate;
    private static Pay? _pay;

    private static INativeDetour? _countDetour, _removeDetour;
    private static CountFn? _countOriginal, _countHook;
    private static RemoveFn? _removeOriginal, _removeHook;

    private static readonly IntPtr[] Arena = new IntPtr[MaterialSeamPolicy.ArenaSlots];
    private static readonly int[] ArenaRows = new int[MaterialSeamPolicy.ArenaSlots];
    private static int _arenaNext;

    [ThreadStatic] private static int _depth;
    private static int _countThrowLogged, _removeThrowLogged, _labelLogged;
    private static bool _enabled;
    private static string _countState = "off", _payState = "off";

    internal static bool CountArmed => _countDetour != null;
    internal static bool PayArmed => _removeDetour != null;

    /// <summary>One line for status and ping: enabled, and what each seam did at install.</summary>
    internal static string Describe() =>
        MaterialSeamPolicy.FormatState(_enabled, Plugin.IsServerProcess, _countState, _payState);

    internal static void BindConfig(ConfigFile config)
    {
        var entry = config.Bind(MaterialSeamPolicy.ConfigSection, MaterialSeamPolicy.ConfigKey, false,
            "Experimental (P3 material sources spike). When true, Bootstrap detours the game's merged material count (client and server) and merged material removal (server) so a registered ctx.MaterialSource provider decides which containers count and pay for building and crafting. Read at process start only; restart to change. Default false: the game's own rules apply and ctx.MaterialSource.Status reports the seams as not installed.");
        _enabled = entry.Value;
    }

    /// <summary>Apply the detours once per process when enabled. Never throws.</summary>
    internal static void Install()
    {
        if (!_enabled)
        {
            ProcessHost.Log?.LogInfo(Describe());
            return;
        }
        try
        {
            var target = NativeMethod.PointerByField(typeof(MergedInventoriesUtility), MaterialSeamPolicy.CountField);
            _countHook = CountHook;
            _countDetour = INativeDetour.CreateAndApply(target, _countHook, out _countOriginal);
            _countState = "armed@0x" + target.ToInt64().ToString("X");
        }
        catch (Exception e)
        {
            _countDetour = null; _countOriginal = null; _countHook = null;
            _countState = "failed (" + e.Message + ")";
        }

        if (Plugin.IsServerProcess)
        {
            try
            {
                var target = NativeMethod.PointerByField(typeof(MergedInventoriesUtility), MaterialSeamPolicy.RemoveField);
                _removeHook = RemoveHook;
                _removeDetour = INativeDetour.CreateAndApply(target, _removeHook, out _removeOriginal);
                _payState = "armed@0x" + target.ToInt64().ToString("X");
            }
            catch (Exception e)
            {
                _removeDetour = null; _removeOriginal = null; _removeHook = null;
                _payState = "failed (" + e.Message + ")";
            }
        }
        ProcessHost.Log?.LogInfo(Describe());
    }

    /// <summary>
    /// Core binds (or, with nulls, clears) its four handlers. False when the seams this side needs are
    /// not armed; Core then reports the seams as not installed.
    /// </summary>
    internal static bool TrySet(Delegate? countGate, Delegate? compose, Delegate? payGate, Delegate? pay)
    {
        if (countGate == null && compose == null && payGate == null && pay == null)
        {
            Volatile.Write(ref _countGate, null);
            Volatile.Write(ref _compose, null);
            Volatile.Write(ref _payGate, null);
            Volatile.Write(ref _pay, null);
            return true;
        }
        if (!CountArmed || (Plugin.IsServerProcess && !PayArmed)) return false;
        try
        {
            var gen = CoreLoader.CurrentPinGeneration;
            var cg = Bind<CountGate>(countGate, "materials.countGate", gen);
            var cc = Bind<CountCompose>(compose, "materials.compose", gen);
            var pg = Bind<PayGate>(payGate, "materials.payGate", gen);
            var p = Bind<Pay>(pay, "materials.pay", gen);
            // Order: the gates last, so a gate never says yes before its partner exists.
            Volatile.Write(ref _compose, cc);
            Volatile.Write(ref _pay, p);
            Volatile.Write(ref _payGate, pg);
            Volatile.Write(ref _countGate, cg);
            Interlocked.Exchange(ref _countThrowLogged, 0);
            Interlocked.Exchange(ref _removeThrowLogged, 0);
            return true;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogError("materials: binding Core's seam handlers failed: " + e.Message);
            return false;
        }
    }

    private static T? Bind<T>(Delegate? d, string name, string? generation) where T : Delegate
    {
        if (d == null) return null;
        var bound = d as T ?? (T)Delegate.CreateDelegate(typeof(T), d.Target, d.Method);
        NativePinRegistry.KeepDelegate(bound, name, generation);
        return bound;
    }

    private static NativeArrayHeader* CountHook(NativeArrayHeader* ret, IntPtr em, ulong heart, ulong target,
        byte includeInteracting, byte includeShared, IntPtr methodInfo)
    {
        var original = _countOriginal!;
        var result = original(ret, em, heart, target, includeInteracting, includeShared, methodInfo);
        if (_depth > 0) return result;
        var gate = Volatile.Read(ref _countGate);
        var compose = Volatile.Read(ref _compose);
        if (gate == null || compose == null) return result;
        _depth++;
        try
        {
            if (!gate(target, heart)) return result;
            if (result->AllocatorLabel != MaterialSeamPolicy.ExpectedAllocatorLabel)
            {
                if (Interlocked.Exchange(ref _labelLogged, 1) == 0)
                    ProcessHost.Log?.LogWarning("materials: the merged count returned allocator label " + result->AllocatorLabel
                        + " (expected " + MaterialSeamPolicy.ExpectedAllocatorLabel + "); availability stays vanilla.");
                return result;
            }
            NativeArrayHeader full = *result;
            NativeArrayHeader bag;
            original(&bag, em, 0UL, target, includeInteracting, includeShared, methodInfo);

            int capacity = MaterialSeamPolicy.InitialCapacity(full.Length, bag.Length);
            int slot = NextSlot();
            IntPtr buffer = Ensure(slot, capacity);
            int n = compose(target, heart, full.Buffer, full.Length, bag.Buffer, bag.Length, buffer, capacity);
            var decision = MaterialSeamPolicy.DecideCount(n, capacity, false);
            if (decision == MaterialSeamPolicy.CountDecision.GrowAndRetry)
            {
                capacity = n;
                buffer = Ensure(slot, capacity);
                n = compose(target, heart, full.Buffer, full.Length, bag.Buffer, bag.Length, buffer, capacity);
                decision = MaterialSeamPolicy.DecideCount(n, capacity, true);
            }
            if (decision != MaterialSeamPolicy.CountDecision.UseComposed) return result;
            result->Buffer = buffer;
            result->Length = n;
            result->AllocatorLabel = MaterialSeamPolicy.ExpectedAllocatorLabel;
            return result;
        }
        catch (Exception e)
        {
            if (Interlocked.Exchange(ref _countThrowLogged, 1) == 0)
                ProcessHost.Log?.LogError("materials: count seam threw; this call kept the game's own counts: " + e);
            return result;
        }
        finally
        {
            _depth--;
        }
    }

    private static void RemoveHook(IntPtr em, ulong heart, ulong target, int itemType, int amount,
        int* remainder, ulong* itemEntity, byte includeInteracting, byte includeShared, byte destroyItem, IntPtr methodInfo)
    {
        var original = _removeOriginal!;
        var gate = Volatile.Read(ref _payGate);
        var pay = Volatile.Read(ref _pay);
        bool governed = false;
        if (_depth == 0 && gate != null && pay != null)
        {
            _depth++;
            try { governed = gate(target, heart, itemType); }
            catch (Exception e)
            {
                governed = false;
                if (Interlocked.Exchange(ref _removeThrowLogged, 1) == 0)
                    ProcessHost.Log?.LogError("materials: payment gate threw; this line was paid by the game's own rules: " + e);
            }
            finally { _depth--; }
        }
        if (!governed)
        {
            original(em, heart, target, itemType, amount, remainder, itemEntity, includeInteracting, includeShared, destroyItem, methodInfo);
            return;
        }

        // The bag and the interaction inventory first, exactly as the game orders them; no castle heart,
        // so the game's own shared storage is skipped.
        original(em, 0UL, target, itemType, amount, remainder, itemEntity, includeInteracting, includeShared, destroyItem, methodInfo);
        int owed = *remainder;
        if (owed <= 0) return;
        _depth++;
        try
        {
            int left = pay!(target, heart, itemType, owed, destroyItem != 0, out ulong taken);
            *remainder = left < 0 ? owed : left;
            if (taken != 0) *itemEntity = taken;
        }
        catch (Exception e)
        {
            *remainder = owed;
            if (Interlocked.Exchange(ref _removeThrowLogged, 1) == 0)
                ProcessHost.Log?.LogError("materials: payment seam threw; the pool paid nothing for this line: " + e);
        }
        finally { _depth--; }
    }

    private static int NextSlot()
    {
        int s = _arenaNext;
        _arenaNext = (s + 1) % MaterialSeamPolicy.ArenaSlots;
        return s;
    }

    /// <summary>A process-lifetime buffer for <paramref name="rows"/> rows; grows, never shrinks, never freed.</summary>
    private static IntPtr Ensure(int slot, int rows)
    {
        if (ArenaRows[slot] >= rows && Arena[slot] != IntPtr.Zero) return Arena[slot];
        int bytes = checked(rows * MaterialSeamPolicy.InventoryBufferStride);
        var fresh = Marshal.AllocHGlobal(bytes);
        // The old buffer may still be referenced by an array the game is reading this frame; keep it.
        Arena[slot] = fresh;
        ArenaRows[slot] = rows;
        return fresh;
    }
}
