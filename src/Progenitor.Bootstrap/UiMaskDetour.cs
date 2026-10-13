using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using BepInEx.Unity.IL2CPP.Hook;

namespace Progenitor.Bootstrap;

/// <summary>
/// Process-lifetime native detour on <c>ProjectM.RotateCameraBlocker.GetConsumedInputs</c> for
/// the mod-UI input mask. Applied at most once per process; Core fills <see cref="TrySetMask"/>
/// and nulls it on Uninstall, and Core must never <c>Dispose</c> this detour.
///
/// <para><b>Why this exists</b> (job FLATCH3, R45 finding 19). Until now Core's
/// <c>GameInputBlocker</c> owned the <c>INativeDetour</c> itself and disposed it on Core
/// Uninstall. Disposing an <c>INativeDetour</c> restores the patched bytes and frees the
/// trampoline the hook calls through, and the game is inside <c>GetConsumedInputs</c> once per
/// frame per registered input context — so that dispose had to happen on the game main thread,
/// and the one caller whose thread is not ours to choose (BepInEx's own
/// <c>BasePlugin.Unload</c>) could do it from anywhere. The failure mode is a jump into freed
/// native memory: a hard crash with no managed stack. FLATCH2 could only narrow the window,
/// because neither posting the dispose (the Core generation carrying the queue is what is going
/// away) nor skipping it (an armed detour over a freed Core delegate is a guaranteed crash,
/// R22 finding 3) is available to a Core-owned handle.</para>
///
/// <para><b>What changes.</b> The handle is Bootstrap's, and Bootstrap is not collectible, so
/// there is no generation whose end forces a dispose. Core teardown becomes
/// <c>Volatile.Write(ref _apply, null)</c> — a pointer store with no native side effect, safe
/// from any thread — and the JMP stays armed for the life of the process pointing at
/// <see cref="Hook"/>, which is a <em>Bootstrap</em> delegate that no ALC unload can collect.
/// A Core generation that comes and goes therefore arms and disarms nothing.</para>
///
/// <para>This is deliberately the same shape as <see cref="ReceiveDetours"/>, down to the
/// unload rule it writes down: null the Core slot, never dispose the detour, because
/// <c>Plugin.Unload</c> is not process exit. That type is the prior art; if the two ever have
/// to differ, say why in both.</para>
///
/// <para><b>Not a protocol bump.</b> Core reaches this type by name through
/// <c>BootstrapLink</c> and treats a missing type or method as "Bootstrap does not offer this",
/// falling back to owning the detour the old way. Old Bootstrap + new Core is the old behavior;
/// new Bootstrap + old Core never calls <see cref="TryInstall"/> and this stays unarmed. Neither
/// pairing is wrong, so <c>CoreLayout.ProtocolVersion</c> does not move.</para>
/// </summary>
public static class UiMaskDetour
{
    /// <summary>
    /// NativePinRegistry name for the Core slot delegate. Deliberately <em>not</em> the site name
    /// Core records in the <c>native</c> ping line ("ui.inputmask"): that one names the resolved
    /// address, which is still Core's to resolve and report, and a second constant claiming
    /// lockstep with it that nothing checks would be a claim, not a guarantee.
    /// </summary>
    private const string PinName = "ui.mask";

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void GetConsumedInputsFn(IntPtr instance, IntPtr blockInputState, IntPtr methodInfo);

    /// <summary>
    /// Core's mask body. The game's own <c>GetConsumedInputs</c> has already run when this is
    /// called, so the slot applies only the mod's subtractions, and the instance guard ("is this
    /// our context?") stays Core's — Bootstrap does not know which <c>RotateCameraBlocker</c> Core
    /// pushed.
    ///
    /// <para>The signature is the detoured method's, <c>methodInfo</c> included, so that Core can
    /// bind the <em>same</em> method it arms its own fallback detour with. That is deliberate:
    /// the mask is then one piece of code with two owners rather than two copies that can drift.
    /// Core's own trampoline field is null on this path, so it does not call the original a second
    /// time.</para>
    /// </summary>
    public delegate void MaskApply(IntPtr instance, IntPtr blockInputState, IntPtr methodInfo);

    private static readonly object Gate = new();

    // Bootstrap-owned, process lifetime. None of these ever references a Core generation, which
    // is the whole point: an ALC unload cannot collect what the armed JMP points at.
    private static INativeDetour? _detour;
    private static GetConsumedInputsFn? _hook;
    private static GetConsumedInputsFn? _original;
    private static IntPtr _address;

    // The one field that does reference collectible Core. Written only through TrySetMask /
    // ClearMask; nulling it is what drops the Core ALC reference.
    private static MaskApply? _apply;

    private static int _inFlight;
    private static int _applyThrowLogged;

    /// <summary>True once the JMP is armed. Never goes back to false for the life of the process.</summary>
    public static bool Owns { get; private set; }

    /// <summary>The address the detour was applied at, or zero. Diagnostics only.</summary>
    public static IntPtr Address => _address;

    /// <summary>
    /// Core-slot invocations currently on the stack. Exposed for the same reason
    /// <see cref="ReceiveDetours.InFlightCount"/> is, but deliberately <b>not</b> wired into
    /// <c>CoreReloadPolicy.MayUnloadCollectibleAlc</c>: the receive detour fires on the network
    /// thread, so a swap running on the main thread can genuinely race it, while
    /// <c>GetConsumedInputs</c> is called only from the game's main-thread input update and the
    /// swap cascade runs on Bootstrap's own FrameTicker tick — one thread cannot be in both. A
    /// route that tears Core down off the main thread <em>and</em> can be followed by an ALC
    /// unload would change that answer, and this counter is what the gate would read.
    /// </summary>
    public static int InFlightCount => Volatile.Read(ref _inFlight);

    /// <summary>Whether a Core generation currently has its mask body in the slot.</summary>
    public static bool HasMask => Volatile.Read(ref _apply) != null;

    /// <summary>
    /// Arm the detour at <paramref name="address"/> (Core resolves it — the address library and
    /// the wanted-list live in Core's <c>Progenitor.Native</c>) and point the slot at
    /// <paramref name="apply"/>. Idempotent: a second Core generation re-fills the slot over the
    /// detour the first one armed. Returns false when the detour could not be applied at all, and
    /// Core then owns the JMP itself as a fallback — the same contract as
    /// <see cref="ReceiveDetours.TrySetClient"/>.
    /// </summary>
    public static bool TryInstall(IntPtr address, Delegate? apply)
    {
        lock (Gate)
        {
            if (Owns)
            {
                if (address != IntPtr.Zero && address != _address)
                {
                    // GameAssembly cannot move in-process, so this means the two sides resolved
                    // different methods. Keep the armed one rather than letting Core apply a
                    // second JMP: two live detours on one input path is worse than a stale one.
                    ProcessHost.Log?.LogWarning(
                        "UiMaskDetour: already applied at 0x" + Hex(_address)
                        + " but Core resolved 0x" + Hex(address)
                        + "; keeping the armed address and taking the mask anyway.");
                }

                return TrySetMaskLocked(apply);
            }

            if (address == IntPtr.Zero) return false;

            try
            {
                _hook = Hook;
                _detour = INativeDetour.CreateAndApply(address, _hook, out _original);
                _address = address;
                Owns = true;
            }
            catch (Exception e)
            {
                _detour = null;
                _original = null;
                _hook = null;
                _address = IntPtr.Zero;
                Owns = false;
                ProcessHost.Log?.LogError(
                    "UiMaskDetour: apply at 0x" + Hex(address)
                    + " failed; Core will own the input-mask detour if it can: " + e);
                return false;
            }

            bool took = TrySetMaskLocked(apply);
            ProcessHost.Log?.LogInfo(
                "UiMaskDetour: input mask detour applied at 0x" + Hex(address)
                + " (process lifetime; Core teardown nulls the slot and never disposes this).");
            return took;
        }
    }

    /// <summary>
    /// Point the trampoline at a Core generation's mask body, or null it. False when this process
    /// never applied the detour, so Core knows the fallback is on it.
    /// </summary>
    public static bool TrySetMask(Delegate? apply)
    {
        lock (Gate) return TrySetMaskLocked(apply);
    }

    /// <summary>
    /// Drop the Core reference. Safe from any thread and from inside the detour: it is one
    /// volatile store, it frees no native memory, and <see cref="Hook"/> re-reads the slot on
    /// every call. This is the operation that replaced a native unpatch on the teardown path.
    /// </summary>
    public static void ClearMask()
    {
        lock (Gate) TrySetMaskLocked(null);
    }

    private static bool TrySetMaskLocked(Delegate? apply)
    {
        if (!Owns) return false;
        try
        {
            MaskApply? bound = null;
            if (apply != null)
            {
                bound = apply is MaskApply typed
                    ? typed
                    : (MaskApply)Delegate.CreateDelegate(typeof(MaskApply), apply.Target, apply.Method);
                NativePinRegistry.KeepDelegate(bound, PinName, CoreLoader.CurrentPinGeneration);
                // Re-arm the once-per-generation throw report: it describes the slot, and the
                // slot is a new generation's method (the FEVT1 rule on ReceiveDetours.TrySetClient).
                Interlocked.Exchange(ref _applyThrowLogged, 0);
            }

            Volatile.Write(ref _apply, bound);
            return true;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("UiMaskDetour: TrySetMask threw: " + e.Message);
            return false;
        }
    }

    /// <summary>
    /// Bootstrap plugin unload. Same rule and same reason as
    /// <see cref="ReceiveDetours.Uninstall"/>: null the Core slot, leave the JMP armed. Bootstrap
    /// is process-lifetime and <c>Plugin.Unload</c> is not process exit, so disposing here would
    /// re-create exactly the use-after-free this type was written to remove — and clearing
    /// <see cref="Owns"/> would let a later Core <c>CreateAndApply</c> on the same address.
    /// </summary>
    internal static void Uninstall() => ClearMask();

    public static string DescribePins() =>
        "pin-diag UiMaskDetour owns=" + (Owns ? "True" : "False")
        + " mask=" + (HasMask ? "True" : "False")
        + " inFlight=" + InFlightCount.ToString(CultureInfo.InvariantCulture)
        + " address=0x" + Hex(_address);

    /// <summary>
    /// The armed trampoline. Bootstrap-owned for the life of the process. The game's own body
    /// always runs first — a Core generation that is down must not change what the game sees —
    /// and the mask is applied only while a slot is present.
    /// </summary>
    private static void Hook(IntPtr instance, IntPtr blockInputState, IntPtr methodInfo)
    {
        var original = Volatile.Read(ref _original);
        try { original?.Invoke(instance, blockInputState, methodInfo); }
        catch (Exception e) { LogOriginalThrowOnce(e); }

        var apply = Volatile.Read(ref _apply);
        if (apply == null) return;

        Interlocked.Increment(ref _inFlight);
        try { apply(instance, blockInputState, methodInfo); }
        catch (Exception e) { LogApplyThrowOnce(e); }
        finally { Interlocked.Decrement(ref _inFlight); }
    }

    private static int _originalThrowLogged;

    private static void LogOriginalThrowOnce(Exception e)
    {
        if (Interlocked.Exchange(ref _originalThrowLogged, 1) != 0) return;
        ProcessHost.Log?.LogWarning(
            "UiMaskDetour: the game's own GetConsumedInputs threw through the trampoline: "
            + e.Message + " (logged once per process).");
    }

    private static void LogApplyThrowOnce(Exception e)
    {
        if (Interlocked.Exchange(ref _applyThrowLogged, 1) != 0) return;
        ProcessHost.Log?.LogWarning(
            "UiMaskDetour: the Core mask body threw: " + e.Message
            + " (logged once per Core generation; re-armed when a generation binds its slot).");
    }

    private static string Hex(IntPtr p) => p.ToInt64().ToString("X", CultureInfo.InvariantCulture);
}
